using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace Ordered;

/// <summary>
/// A concurrent, sorted <see cref="IDictionary{TKey,TValue}"/> backed by an in-memory B+ tree and kept
/// thread-safe with Optimistic Lock Coupling (OLC). Think of it as the ordered, range-queryable sibling of
/// <see cref="System.Collections.Concurrent.ConcurrentDictionary{TKey,TValue}"/>: because keys are held in
/// sort order it adds navigable lookups (<c>TryGetCeiling</c>/<c>TryGetFloor</c>/<c>TryGetHigher</c>/
/// <c>TryGetLower</c>) and live range views (<c>GetViewBetween</c>, <c>GetViewFrom</c>, <c>GetViewTo</c>,
/// <c>Reverse</c>) in the spirit of <see cref="SortedSet{T}"/>.
///
/// <para><b>Consistency.</b> Point operations — <see cref="TryGetValue"/>, <see cref="TryAdd"/>,
/// <see cref="TryRemove(TKey, out TValue)"/>, the indexer, <see cref="GetOrAdd(TKey, TValue)"/>,
/// <c>AddOrUpdate</c>, and the navigable lookups — are <i>linearizable</i>. Enumeration and range views are
/// <i>weakly consistent</i>: they never throw on concurrent mutation and always yield keys in order, but
/// are not a point-in-time snapshot (like <see cref="System.Collections.Concurrent.ConcurrentDictionary{TKey,TValue}"/>'s
/// enumerator). <see cref="Count"/> is weakly consistent and never negative. See <c>docs/CONCURRENCY.md</c>.</para>
///
/// <para><b>How it stays correct under contention.</b> Reads are optimistic and never block: a reader
/// snapshots a per-node version, descends, and re-validates; any concurrent write bumps the version and
/// triggers a cheap retry rather than a stall. (On weak-memory hardware the re-validation carries a load
/// fence — see <c>Node.Validate</c>.) Writes latch-couple top-down, releasing an ancestor the moment it is
/// known it cannot split; splits propagate up the locked chain and recursive sibling merges reclaim space
/// automatically. Locks are always taken parent-before-child, so the structure cannot deadlock.</para>
///
/// <para>The <c>order</c> constructor parameter sets node fan-out (max keys per node; default 64).</para>
/// </summary>
public sealed class ConcurrentBTreeDictionary<TKey, TValue>
    : IDictionary<TKey, TValue>, IReadOnlyDictionary<TKey, TValue>
{
    // ---------------------------------------------------------------------
    //  Nodes + per-node optimistic lock (version word: even = free, odd = write-locked)
    // ---------------------------------------------------------------------
    private abstract class Node
    {
        internal long Version;          // even = unlocked; odd = write-locked
        internal int Count;             // keys in use
        internal TKey[] Keys = default!;

        internal void WriteLock()
        {
            var sw = new SpinWait();
            for (; ; )
            {
                long v = Volatile.Read(ref Version);
                if ((v & 1L) == 0 && Interlocked.CompareExchange(ref Version, v + 1, v) == v) return;
                sw.SpinOnce();
            }
        }

        internal void WriteUnlock() => Volatile.Write(ref Version, Volatile.Read(ref Version) + 1); // odd -> even

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal bool TryReadVersion(out long v)        // true if currently unlocked
        {
            v = Volatile.Read(ref Version);
            return (v & 1L) == 0;
        }

        // On weak-memory hardware (ARM/Apple silicon) an acquire-load alone does NOT stop the
        // optimistic DATA reads that precede this check (Children[ci], Keys/Values, leaf links)
        // from being reordered AFTER the version read — so we could validate a node as unchanged
        // yet have read a child slot a concurrent merge already nulled (NRE) or a torn key/value.
        // A LoadLoad fence before the version read closes the seqlock. x86/x64 is TSO (loads never
        // reorder with loads), so the fence is gated off there and adds zero cost on that path.
        private static readonly bool FenceBeforeValidate =
            RuntimeInformation.ProcessArchitecture is Architecture.Arm64 or Architecture.Arm;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal bool Validate(long v)
        {
            if (FenceBeforeValidate) Interlocked.MemoryBarrier();
            return Volatile.Read(ref Version) == v;                          // unchanged since we read v
        }

        // Optimistically upgrade to a write lock: succeeds only if the version is still `observed`
        // (i.e. the node hasn't been touched since we read it during the optimistic descent).
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal bool TryUpgrade(long observed)
            => Interlocked.CompareExchange(ref Version, observed + 1, observed) == observed;
    }

    private sealed class Leaf : Node
    {
        internal TValue[] Values = default!;
        internal Leaf? Next;            // right sibling (ascending)
        internal Leaf? Prev;            // left sibling (descending) — doubly-linked for O(k) reverse scans
    }

    private sealed class Internal : Node
    {
        internal Node[] Children = default!;   // Count + 1 in use
    }

    private readonly IComparer<TKey> _cmp;
    private readonly int _max;
    private readonly int _min;          // merge target: a merge cascade packs survivors back up to >= _min (order/2)
    private readonly int _mergeBelow;   // merge TRIGGER, hardcoded to order/3 (see ctor)

    private volatile Node _root;
    private readonly StripedCounter _count = new();

    /// <param name="order">Node fan-out (max keys per node). Default 64.</param>
    /// <param name="comparer">Key ordering; defaults to <see cref="Comparer{T}.Default"/>.</param>
    public ConcurrentBTreeDictionary(int order = 64, IComparer<TKey>? comparer = null)
    {
        if (order < 3) throw new ArgumentOutOfRangeException(nameof(order), "order must be >= 3");
        _max = order;
        _min = order / 2;
        // Merge TRIGGER, hardcoded at order/3. Deletes free a key immediately; a *structural* merge is only
        // attempted once a leaf falls below order/3 — well under the order/2 fill a split leaves behind — so
        // ordinary churn never oscillates across the threshold. That avoids split/merge "thrash", which would
        // otherwise lock upper nodes constantly and storm concurrent optimistic reads with restarts (measured
        // ~14 restarts/read at the half-full trigger -> ~0 here, roughly doubling contended read throughput).
        // When it does fire, the merge cascade still packs survivors back up to >= order/2, so reclamation
        // stays effective and automatic — no manual compaction. order/3 balances that reclamation against the
        // throughput win (lower would scale reads further but leave leaves emptier; this is the chosen point).
        _mergeBelow = Math.Max(1, order / 3);
        _cmp = comparer ?? Comparer<TKey>.Default;
        _root = NewLeaf();
    }

    // Sum() is a weakly-consistent striped read (LongAdder semantics) -> can momentarily be < 0 mid-churn; clamp.
    public int Count { get { long c = _count.Sum(); return c <= 0 ? 0 : c >= int.MaxValue ? int.MaxValue : (int)c; } }
    public bool IsEmpty => _count.Sum() <= 0;

    private Leaf NewLeaf() => new() { Keys = new TKey[_max + 1], Values = new TValue[_max + 1] };
    private Internal NewInternal() => new() { Keys = new TKey[_max + 1], Children = new Node[_max + 2] };

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int Find(TKey[] keys, int count, TKey key)
    {
        int lo = 0, hi = count - 1;
        while (lo <= hi)
        {
            int mid = (int)(((uint)lo + (uint)hi) >> 1);
            int c = _cmp.Compare(key, keys[mid]);
            if (c == 0) return mid;
            if (c < 0) hi = mid - 1; else lo = mid + 1;
        }
        return ~lo;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int ChildIndex(TKey[] keys, int count, TKey key)
    {
        int lo = 0, hi = count;
        while (lo < hi)
        {
            int mid = (int)(((uint)lo + (uint)hi) >> 1);
            if (_cmp.Compare(key, keys[mid]) >= 0) lo = mid + 1; else hi = mid;
        }
        return lo;
    }

    // =====================================================================
    //  Lookup — optimistic, lock-free
    // =====================================================================
    public bool TryGetValue(TKey key, out TValue value)
    {
        if (key == null) throw new ArgumentNullException(nameof(key));
        for (; ; ) // restart
        {
            Node cur = _root;
            if (!cur.TryReadVersion(out long v)) continue;   // node locked -> restart
            if (cur != _root) continue;                       // root advanced -> restart

            while (cur is Internal ind)
            {
                int ci = ChildIndex(ind.Keys, ind.Count, key);
                Node child = ind.Children[ci];
                if (!ind.Validate(v)) goto restart;       // routing read consistent
                // Fix B: if the child is briefly write-locked, SPIN on it (cheap) rather than re-descending
                // from the root (expensive). Bail to restart only if the PARENT changes while we wait — then
                // our routing may be stale. Mirrors the B-link reads; kills the descent-into-locked-leaf storm.
                long cv;
                var sw = new SpinWait();
                while (!child.TryReadVersion(out cv))
                {
                    sw.SpinOnce();
                    if (!ind.Validate(v)) goto restart;
                }
                if (!ind.Validate(v)) goto restart;       // parent unchanged through the child read
                cur = child; v = cv;
            }

            var leaf = (Leaf)cur;
            int cnt = Volatile.Read(ref leaf.Count);
            int i = Find(leaf.Keys, cnt, key);
            bool found = i >= 0;
            TValue val = found ? leaf.Values[i] : default!;
            if (!leaf.Validate(v)) goto restart;              // leaf unchanged during our read
            value = val;
            return found;

        restart:;
        }
    }

    public bool ContainsKey(TKey key) => TryGetValue(key, out _);

    public TValue this[TKey key]
    {
        get => TryGetValue(key, out var v) ? v : throw new KeyNotFoundException();
        set => Insert(key, value, onlyIfAbsent: false);
    }

    public bool TryAdd(TKey key, TValue value) => Insert(key, value, onlyIfAbsent: true);

    // =====================================================================
    //  Insert — latch-coupling with safe-node release + split propagation
    // =====================================================================
    [ThreadStatic] private static Node[]? _held;

    private Node LockRoot()
    {
        for (; ; )
        {
            Node r = _root;
            r.WriteLock();
            if (r == _root) return r;
            r.WriteUnlock();
        }
    }

    // A node is "insert-safe" if a separator pushed into it cannot overflow it.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool InsertSafe(Node n) => n.Count < _max;

    // Optimistically descend (no locks) to the leaf that should hold `key`; return it and the
    // version observed for it. Returns false (caller retries) if a node was locked mid-descent.
    private bool TryDescendToLeaf(TKey key, out Leaf leaf, out long version)
    {
        leaf = null!; version = 0;
        Node cur = _root;
        if (!cur.TryReadVersion(out long v)) return false;
        if (cur != _root) return false;                          // root advanced -> retry
        while (cur is Internal ind)
        {
            int ci = ChildIndex(ind.Keys, ind.Count, key);
            Node child = ind.Children[ci];
            if (!ind.Validate(v)) return false;                  // child pointer + routing read was consistent
            if (!child.TryReadVersion(out long cv)) return false;
            if (!ind.Validate(v)) return false;                  // parent unchanged THROUGH the child read -> routing still valid
            cur = child; v = cv;
        }
        leaf = (Leaf)cur; version = v;
        return true;
    }

    // Fast path: optimistic descent + lock ONLY the leaf. If the upgrade succeeds the leaf is
    // unchanged since we read it, which guarantees `key` still belongs in this leaf (a key can only
    // migrate to another leaf via a split, which would have bumped the version). If the leaf has room
    // we finish here — no root/ancestor locks. Otherwise we fall back to latch-coupling for the split.
    private bool Insert(TKey key, TValue value, bool onlyIfAbsent)
    {
        if (key == null) throw new ArgumentNullException(nameof(key));
        for (; ; )
        {
            if (!TryDescendToLeaf(key, out var leaf, out long v)) continue;
            if (!leaf.TryUpgrade(v)) continue;                   // leaf changed under us -> retry

            if (leaf.Count < _max)                               // has room -> no split, finish on the leaf
            {
                int idx = Find(leaf.Keys, leaf.Count, key);
                bool added;
                if (idx >= 0) { if (!onlyIfAbsent) leaf.Values[idx] = value; added = false; }
                else { InsertIntoLeaf(leaf, ~idx, key, value); added = true; }
                leaf.WriteUnlock();
                if (added) _count.Increment();
                return added;
            }

            leaf.WriteUnlock();                                  // full -> needs a split
            return InsertPessimistic(key, value, onlyIfAbsent);
        }
    }

    private bool InsertPessimistic(TKey key, TValue value, bool onlyIfAbsent)
    {
        Node[] held = _held ??= new Node[48];
        int hc = 0;

        Node root = LockRoot();
        held[hc++] = root;
        Node cur = root;

        while (cur is Internal ind)
        {
            int ci = ChildIndex(ind.Keys, ind.Count, key);
            Node child = ind.Children[ci];
            child.WriteLock();
            if (InsertSafe(child))                       // child can absorb any split from below
            {
                for (int i = 0; i < hc; i++) held[i].WriteUnlock();   // release all current ancestors
                hc = 0;
            }
            held[hc++] = child;
            cur = child;
        }

        // cur is the leaf (held[hc-1]); held[0..hc-1] is the locked chain from the deepest safe node down.
        var leaf = (Leaf)cur;
        int idx = Find(leaf.Keys, leaf.Count, key);
        bool added;
        if (idx >= 0)
        {
            if (!onlyIfAbsent) leaf.Values[idx] = value;   // replace (we hold the leaf's write lock)
            added = false;
        }
        else
        {
            InsertIntoLeaf(leaf, ~idx, key, value);
            added = true;
            if (leaf.Count > _max)
            {
                SplitLeaf(leaf, out TKey sep, out Node right);
                Propagate(held, hc, sep, right);
            }
        }

        for (int i = 0; i < hc; i++) held[i].WriteUnlock();
        if (added) _count.Increment();
        return added;
    }

    // Propagate a (separator, rightChild) split up through the locked held chain. held[hc-1] is the
    // node that just split; insert into held[hc-2], splitting further as needed; grow the root if the
    // topmost held node (the root) splits.
    private void Propagate(Node[] held, int hc, TKey sep, Node right)
    {
        for (int i = hc - 2; i >= 0; i--)
        {
            var parent = (Internal)held[i];
            int ci = ChildIndex(parent.Keys, parent.Count, sep);
            InsertIntoInternal(parent, ci, sep, right);
            if (parent.Count <= _max) return;            // absorbed
            SplitInternal(parent, out sep, out right);   // continue up
        }
        // topmost held node split -> grow a new root (held[0] is the old root, still locked)
        var old = held[0];
        var newRoot = NewInternal();
        newRoot.Keys[0] = sep;
        newRoot.Children[0] = old;
        newRoot.Children[1] = right;
        newRoot.Count = 1;
        _root = newRoot;                                 // publish while still holding old root's lock
    }

    private static void InsertIntoLeaf(Leaf leaf, int at, TKey key, TValue value)
    {
        Array.Copy(leaf.Keys, at, leaf.Keys, at + 1, leaf.Count - at);
        Array.Copy(leaf.Values, at, leaf.Values, at + 1, leaf.Count - at);
        leaf.Keys[at] = key;
        leaf.Values[at] = value;
        leaf.Count++;     // count is the last field touched -> readers see a consistent prefix (and re-validate)
    }

    private static void InsertIntoInternal(Internal n, int ci, TKey sep, Node right)
    {
        Array.Copy(n.Keys, ci, n.Keys, ci + 1, n.Count - ci);
        Array.Copy(n.Children, ci + 1, n.Children, ci + 2, n.Count - ci);
        n.Keys[ci] = sep;
        n.Children[ci + 1] = right;
        n.Count++;
    }

    private void SplitLeaf(Leaf leaf, out TKey sep, out Node rightNode)
    {
        int mid = (_max + 1) / 2;
        int rc = leaf.Count - mid;
        var right = NewLeaf();
        Array.Copy(leaf.Keys, mid, right.Keys, 0, rc);
        Array.Copy(leaf.Values, mid, right.Values, 0, rc);
        right.Count = rc;
        // Splice `right` between `leaf` and its old right sibling, fixing the doubly-linked chain.
        // `leaf` is already write-locked (held). We briefly lock the old right sibling to fix its Prev;
        // sibling locks are always acquired left→right, so this can't deadlock with the top-down latches.
        var oldNext = leaf.Next;
        right.Prev = leaf;
        right.Next = oldNext;
        if (oldNext != null) { oldNext.WriteLock(); oldNext.Prev = right; oldNext.WriteUnlock(); }
        leaf.Next = right;                               // publish in the forward chain last
        leaf.Count = mid;
        Array.Clear(leaf.Keys, mid, rc);
        Array.Clear(leaf.Values, mid, rc);
        sep = right.Keys[0];
        rightNode = right;
    }

    private void SplitInternal(Internal node, out TKey sep, out Node rightNode)
    {
        int mid = node.Count / 2;                 // node.Keys[mid] moves UP
        int rKeys = node.Count - mid - 1;
        var right = NewInternal();
        Array.Copy(node.Keys, mid + 1, right.Keys, 0, rKeys);
        Array.Copy(node.Children, mid + 1, right.Children, 0, rKeys + 1);
        right.Count = rKeys;
        sep = node.Keys[mid];
        Array.Clear(node.Keys, mid, rKeys + 1);          // keys[mid .. _max]
        Array.Clear(node.Children, mid + 1, rKeys + 1);  // children[mid+1 .. _max+1]
        node.Count = mid;
        rightNode = right;
    }

    // =====================================================================
    //  Delete — lazy (lock-couple to the leaf, remove; deletes never split, so always safe)
    // =====================================================================
    public bool TryRemove(TKey key, out TValue value)
    {
        if (key == null) throw new ArgumentNullException(nameof(key));
        // Lazy delete never splits/merges, so a leaf is always "safe": optimistic descent + lock only
        // the leaf. An unchanged leaf (upgrade succeeded) is guaranteed to own `key` if present.
        for (; ; )
        {
            if (!TryDescendToLeaf(key, out var leaf, out long v)) continue;
            if (!leaf.TryUpgrade(v)) continue;

            int i = Find(leaf.Keys, leaf.Count, key);
            if (i < 0) { leaf.WriteUnlock(); value = default!; return false; }
            value = leaf.Values[i];
            Array.Copy(leaf.Keys, i + 1, leaf.Keys, i, leaf.Count - i - 1);
            Array.Copy(leaf.Values, i + 1, leaf.Values, i, leaf.Count - i - 1);
            leaf.Count--;
            Array.Clear(leaf.Keys, leaf.Count, 1);
            Array.Clear(leaf.Values, leaf.Count, 1);
            bool underfull = leaf.Count < _mergeBelow;   // lazier trigger -> no thrash (fix A)
            leaf.WriteUnlock();
            _count.Decrement();
            if (underfull) TryMergeUnderfullLeaf(key);    // best-effort space reclamation (sibling merge)
            return true;
        }
    }

    /// <summary>Removes the pair only if both key and current value match. (ConcurrentMap.remove(k,v).)</summary>
    public bool TryRemove(KeyValuePair<TKey, TValue> item) => TryRemoveIf(item.Key, item.Value, out _);

    private bool TryRemoveIf(TKey key, TValue expected, out TValue removed)
    {
        if (key == null) throw new ArgumentNullException(nameof(key));
        for (; ; )
        {
            if (!TryDescendToLeaf(key, out var leaf, out long v)) continue;
            if (!leaf.TryUpgrade(v)) continue;
            int i = Find(leaf.Keys, leaf.Count, key);
            if (i < 0 || !EqualityComparer<TValue>.Default.Equals(leaf.Values[i], expected)) { leaf.WriteUnlock(); removed = default!; return false; }
            removed = leaf.Values[i];
            Array.Copy(leaf.Keys, i + 1, leaf.Keys, i, leaf.Count - i - 1);
            Array.Copy(leaf.Values, i + 1, leaf.Values, i, leaf.Count - i - 1);
            leaf.Count--;
            Array.Clear(leaf.Keys, leaf.Count, 1);
            Array.Clear(leaf.Values, leaf.Count, 1);
            bool underfull = leaf.Count < _mergeBelow;   // lazier trigger -> no thrash (fix A)
            leaf.WriteUnlock();
            _count.Decrement();
            if (underfull) TryMergeUnderfullLeaf(key);    // best-effort space reclamation (sibling merge)
            return true;
        }
    }

    // =====================================================================
    //  Space reclamation — LEAF SIBLING MERGE so delete-heavy churn doesn't leak nodes.
    //
    //  Lazy delete leaves underfull leaves; under scattered deletion a leaf rarely empties outright,
    //  so unlinking only EMPTY leaves reclaims almost nothing. Here, a delete that leaves a leaf below
    //  half-full triggers a merge with an adjacent sibling: the right node's entries are appended to
    //  the left node and the right node + its separator are removed from the parent. Two ≤half-full
    //  leaves always fit (sum < _max), so scattered churn coalesces back toward ~half occupancy and
    //  the leaf count tracks the live set — not the build-time peak.
    //
    //  Concurrency: we DON'T lock the root (that would serialize every merge and fight every root
    //  split — wrecking write scalability). We optimistically descend to the leaf's PARENT, upgrade
    //  just that parent, then lock the adjacent leaf pair in the tree's global order (parent ↓, then
    //  leaves strictly left→right: leftNode, rightNode, rightNode.Next). Same discipline SplitLeaf
    //  uses, so it can't deadlock with concurrent splits/inserts. All three leaf locks are held across
    //  the whole mutation, so a scanner sees the trio either fully pre- or fully post-merge.
    //
    //  Scan-safety: right's entries move INTO left, so a scan that snapshotted left's OLD entries must
    //  not then skip right. Two things guarantee no missed key: (1) we never destroy right's entries —
    //  it stays readable for a scanner that followed the old left.Next==right; (2) scans read each
    //  leaf's entries and its Next/Prev atomically under one version (see SnapshotLeaf), so the link a
    //  scan follows always matches the entries it just saw. When a leaf merge would underflow a non-root
    //  parent, we hand off to MergePessimistic, which cascades the merge up the tree and collapses a
    //  single-child root — so a full drain returns all the way to a single-leaf root with no skeleton.
    // =====================================================================

    // Optimistic descent that stops at the leaf's PARENT. Returns false (caller gives up — this is
    // best-effort) if the root is a bare leaf, or a node was locked / shifted mid-descent.
    private bool TryDescendToParent(TKey key, out Internal parent, out int ci, out long pver, out Leaf leaf, out long lver)
    {
        parent = null!; ci = 0; pver = 0; leaf = null!; lver = 0;
        Node cur = _root;
        if (cur is not Internal) return false;                   // no parent to edit (root is a leaf)
        if (!cur.TryReadVersion(out long v)) return false;
        if (cur != _root) return false;
        var par = (Internal)cur; long pv = v;
        for (; ; )
        {
            int index = ChildIndex(par.Keys, par.Count, key);
            Node child = par.Children[index];
            if (!par.Validate(pv)) return false;                 // routing read consistent
            if (!child.TryReadVersion(out long cv)) return false;
            if (!par.Validate(pv)) return false;                 // parent unchanged THROUGH the child read
            if (child is Leaf lf) { parent = par; ci = index; pver = pv; leaf = lf; lver = cv; return true; }
            par = (Internal)child; pv = cv;
        }
    }

    private void TryMergeUnderfullLeaf(TKey key)
    {
        // A single merge can leave the survivor still underfull (e.g. two empty leaves -> one empty leaf),
        // which nothing else would re-trigger (an empty leaf never receives another delete). So keep
        // merging toward `key` until the surviving leaf is at least half full or no further merge applies.
        for (int guard = 0; guard < 64 && MergeUnderfullLeafOnce(key); guard++) { }
    }

    // One merge step for the leaf on the path to `key`. Returns true iff it merged AND the survivor is
    // still underfull (so the caller should loop). Returns false when the leaf is already healthy, the
    // sibling is too full to merge, or the work was handed to the pessimistic cascade.
    private bool MergeUnderfullLeafOnce(TKey key)
    {
        for (int attempt = 0; attempt < 2; attempt++)           // bounded: contended parent -> brief retry
        {
            if (!TryDescendToParent(key, out var parent, out int ci, out long pv, out _, out _))
                return false;

            if (!parent.TryUpgrade(pv)) continue;               // parent changed under us -> retry
            int pc = parent.Count;                              // separators; children = pc+1 (stable: parent locked)
            bool parentIsRoot = ReferenceEquals(parent, _root);
            // Cascade cases — merging the leaf here would underflow a NON-root parent, or the parent is
            // already a single-child skeleton: hand off to the latch-coupled path that merges up to the
            // root and collapses a single-child root. The fast path below handles the common case where
            // the parent stays healthy (or is the root) with no root lock.
            if (pc == 0 || (pc <= _min && !parentIsRoot))
            {
                parent.WriteUnlock();
                return MergePessimistic(key);                   // returns whether the survivor stays underfull
            }

            // Adjacent pair (li, li+1): merge the underfull leaf with its right sibling, or — if it's the
            // rightmost child — merge it into its left sibling. Either way li < li+1 (left→right).
            int li = (ci < pc) ? ci : ci - 1;
            var left = (Leaf)parent.Children[li];
            var right = (Leaf)parent.Children[li + 1];

            left.WriteLock();                                   // left sibling first (global left→right order)
            right.WriteLock();
            var target = (ci == li) ? left : right;             // the leaf on the path to `key`
            if (target.Count >= _min || left.Count + right.Count > _max)
            {                                                   // already healthy, or sibling too full to merge
                right.WriteUnlock(); left.WriteUnlock(); parent.WriteUnlock();
                return false;
            }
            Leaf? rnext = Volatile.Read(ref right.Next);
            if (rnext != null) rnext.WriteLock();

            // append right's entries to left (left's keys are all < right's, so order is preserved)
            int lc = left.Count, rc = right.Count;
            Array.Copy(right.Keys, 0, left.Keys, lc, rc);
            Array.Copy(right.Values, 0, left.Values, lc, rc);
            Volatile.Write(ref left.Next, rnext);               // splice right out of the forward chain
            if (rnext != null) Volatile.Write(ref rnext.Prev, left);
            left.Count = lc + rc;                               // publish count LAST (readers re-validate version)
            // (right keeps its entries + Next/Prev intact: a scan that already followed left.Next==right
            //  still reads right's keys; dedup tolerates the transient overlap with left.)

            RemoveChild(parent, li + 1);                        // drop right child + separator Keys[li]

            bool collapseRoot = ReferenceEquals(parent, _root) && parent.Count == 0;
            Node? newRoot = collapseRoot ? parent.Children[0] : null;
            bool survivorUnderfull = left.Count < _min && !collapseRoot;

            if (rnext != null) rnext.WriteUnlock();
            right.WriteUnlock();
            left.WriteUnlock();
            if (collapseRoot) _root = newRoot!;                 // publish new root while still holding old root's lock
            parent.WriteUnlock();
            return survivorUnderfull;
        }
        return false;
    }

    // Remove child pointer `ci` (0..Count) and its adjacent separator from an internal node.
    private static void RemoveChild(Internal n, int ci)
    {
        int c = n.Count;                                         // c separators, c+1 children
        int sep = ci > 0 ? ci - 1 : 0;                           // separator that bounded the removed child
        Array.Copy(n.Keys, sep + 1, n.Keys, sep, c - 1 - sep);
        Array.Copy(n.Children, ci + 1, n.Children, ci, c - ci);
        n.Count = c - 1;
        Array.Clear(n.Keys, c - 1, 1);
        Array.Clear(n.Children, c, 1);
    }

    // =====================================================================
    //  Recursive (cascading) merge — eliminates the single-child internal skeleton that leaf merge
    //  alone leaves behind. Used as the FALLBACK when a leaf merge would underflow a non-root parent.
    //
    //  Pessimistic latch-coupling, mirroring InsertPessimistic but for deletion: descend from the root
    //  locking each node, and RELEASE all ancestors once we reach a "delete-safe" one (Count > _min —
    //  it can absorb one child removal without underflowing), so the root lock is dropped in the common
    //  case. We merge the leaf pair, then walk the held chain UP, merging each underfull internal with a
    //  sibling (which removes a child from its parent) until a node stays >= _min, then collapse a
    //  single-child root. The deepest safe ancestor caps the cascade, so we never need a lock above what
    //  we hold. Lock order is the tree's global order (parent ↓, siblings left→right), so no deadlock
    //  with splits/inserts/other merges.
    // =====================================================================
    [ThreadStatic] private static Node[]? _heldM;

    // Returns true iff the merged leaf survivor is still underfull (caller should loop another merge).
    private bool MergePessimistic(TKey key)
    {
        Node[] held = _heldM ??= new Node[64];
        int hc = 0;

        Node root = LockRoot();
        if (root is Leaf) { root.WriteUnlock(); return false; } // shrank to a single leaf — nothing to merge
        held[hc++] = root;
        Node cur = root;

        // Descend to the leaf's parent, latch-coupling with delete-safe ancestor release.
        while (true)
        {
            var ind = (Internal)cur;
            int ci = ChildIndex(ind.Keys, ind.Count, key);
            Node child = ind.Children[ci];
            if (child is Leaf) break;                            // `ind` is the leaf's parent
            child.WriteLock();
            if (((Internal)child).Count > _min)                 // delete-safe: it absorbs one child removal
            {
                for (int i = 0; i < hc; i++) held[i].WriteUnlock();
                hc = 0;
            }
            held[hc++] = child;
            cur = child;
        }

        var P = (Internal)held[hc - 1];                         // leaf's parent (locked)
        // Every held node stays LOCKED through the whole operation (MergeInternalLevel re-locks the node
        // it touches), so we just unlock the entire held chain at the end.

        // --- leaf level: merge the underfull leaf with a sibling (if it has one and they fit) ---
        bool survivorUnderfull = false;
        int pc = P.Count;
        if (pc > 0)
        {
            int ci = ChildIndex(P.Keys, pc, key);
            int lo = (ci < pc) ? ci : ci - 1, hi = lo + 1;
            var left = (Leaf)P.Children[lo];
            var right = (Leaf)P.Children[hi];
            left.WriteLock();
            right.WriteLock();
            if (left.Count + right.Count <= _max)
            {
                Leaf? rnext = Volatile.Read(ref right.Next);
                if (rnext != null) rnext.WriteLock();
                int lc = left.Count, rc = right.Count;
                Array.Copy(right.Keys, 0, left.Keys, lc, rc);
                Array.Copy(right.Values, 0, left.Values, lc, rc);
                Volatile.Write(ref left.Next, rnext);
                if (rnext != null) Volatile.Write(ref rnext.Prev, left);
                left.Count = lc + rc;
                RemoveChild(P, hi);                             // P.Count--
                survivorUnderfull = left.Count < _min;         // still underfull? caller loops another merge
                if (rnext != null) rnext.WriteUnlock();
                right.WriteUnlock();
                left.WriteUnlock();
            }
            else
            {
                right.WriteUnlock(); left.WriteUnlock();        // sibling too full to merge -> give up
                for (int i = 0; i < hc; i++) held[i].WriteUnlock();
                return false;
            }
        }
        // (pc == 0: P is a single-child skeleton internal; no leaf merge, but P is underfull -> propagate.)

        // --- propagate up: merge each underfull internal with a sibling until one stays healthy ---
        int level = hc - 1;
        while (level >= 1)
        {
            if (((Internal)held[level]).Count >= _min) break;   // healthy -> stop
            var gp = (Internal)held[level - 1];
            if (gp.Count == 0) { level--; continue; }           // gp single-child: no sibling for held[level] here;
                                                                // gp is itself underfull -> handle it one level up
            int idx = ChildIndex(gp.Keys, gp.Count, key);       // held[level]'s index within gp (key routes there)
            if (!MergeInternalLevel(gp, idx, (Internal)held[level])) break;   // sibling too full -> stop
            level--;                                            // gp.Count-- ; keep climbing
        }

        // --- root collapse: a single-child internal root becomes its only child ---
        if (ReferenceEquals(held[0], _root) && held[0] is Internal ri && ri.Count == 0)
            _root = ri.Children[0];

        for (int i = 0; i < hc; i++) held[i].WriteUnlock();     // every held node was kept locked
        return survivorUnderfull;
    }

    // Merge the underfull internal `child` (= parent.Children[idx], locked) with an adjacent sibling,
    // chosen as the pair (lo, hi) around idx and locked left→right (in the rightmost case we briefly
    // release+relock `child` to keep that order — safe because holding `parent` pins its child set).
    // The separator parent.Keys[lo] is pulled down between the two halves and the right node is dropped
    // from `parent`. Caller contract: `child` and `parent` REMAIN LOCKED on return (the sibling is
    // unlocked internally); the caller unlocks the whole held chain at the end. Returns false (parent
    // unchanged) if the two wouldn't fit one node. Precondition: parent.Count >= 1 (it has a sibling).
    private bool MergeInternalLevel(Internal parent, int idx, Internal child)
    {
        int gc = parent.Count;
        int lo = (idx < gc) ? idx : idx - 1, hi = lo + 1;
        if (lo == idx)                                          // child is the LEFT member of the pair
        {
            Internal L = child;                                // already locked
            var R = (Internal)parent.Children[hi];
            R.WriteLock();                                      // right sibling — locked after child (order ok)
            if (L.Count + 1 + R.Count > _max) { R.WriteUnlock(); return false; }
            MergeInternalInto(L, parent.Keys[lo], R);
            RemoveChild(parent, hi);                            // drop the sibling R; child (L) survives, stays locked
            R.WriteUnlock();
            return true;
        }
        else                                                   // child is the rightmost: relock the pair in order
        {
            child.WriteUnlock();                               // release to acquire the left sibling first
            var L = (Internal)parent.Children[lo];
            L.WriteLock();
            var R = (Internal)parent.Children[hi];             // == child, re-locked as the right member
            R.WriteLock();
            if (L.Count + 1 + R.Count > _max) { L.WriteUnlock(); return false; }   // child (R) stays locked
            MergeInternalInto(L, parent.Keys[lo], R);
            RemoveChild(parent, hi);                            // drop R == child (detached but kept locked for caller)
            L.WriteUnlock();                                   // sibling L (the survivor) unlocked
            return true;
        }
    }

    // L absorbs [L's entries, pulled-down separator, R's entries]; their child arrays concatenate.
    private static void MergeInternalInto(Internal L, TKey separator, Internal R)
    {
        int lc = L.Count, rc = R.Count;
        L.Keys[lc] = separator;
        Array.Copy(R.Keys, 0, L.Keys, lc + 1, rc);
        Array.Copy(R.Children, 0, L.Children, lc + 1, rc + 1);
        L.Count = lc + 1 + rc;
    }

    // =====================================================================
    //  Conditional replace (TryUpdate) — lock the leaf and CAS the value in place.
    // =====================================================================
    private bool DoReplace(TKey key, TValue newValue, bool hasComparison, TValue comparison)
    {
        if (key == null) throw new ArgumentNullException(nameof(key));
        for (; ; )
        {
            if (!TryDescendToLeaf(key, out var leaf, out long v)) continue;
            if (!leaf.TryUpgrade(v)) continue;
            int i = Find(leaf.Keys, leaf.Count, key);
            if (i < 0) { leaf.WriteUnlock(); return false; }
            if (hasComparison && !EqualityComparer<TValue>.Default.Equals(leaf.Values[i], comparison)) { leaf.WriteUnlock(); return false; }
            leaf.Values[i] = newValue;
            leaf.WriteUnlock();
            return true;
        }
    }

    public bool TryUpdate(TKey key, TValue newValue, TValue comparisonValue) => DoReplace(key, newValue, true, comparisonValue);

    // =====================================================================
    //  NavigableMap: relational queries + first/last + poll, via the leaf chain.
    // =====================================================================

    // first index with keys[i] >= key (lower bound); ChildIndex already gives the upper bound (> key).
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int LowerBound(TKey[] keys, int count, TKey key)
    {
        int lo = 0, hi = count;
        while (lo < hi) { int mid = (int)(((uint)lo + (uint)hi) >> 1); if (_cmp.Compare(keys[mid], key) < 0) lo = mid + 1; else hi = mid; }
        return lo;
    }

    private Leaf RightmostLeaf()
    {
        for (; ; )
        {
            Node cur = _root;
            if (!cur.TryReadVersion(out long v)) continue;
            while (cur is Internal ind)
            {
                Node child = ind.Children[ind.Count];
                if (!ind.Validate(v)) goto restart;
                if (!child.TryReadVersion(out long cv)) goto restart;
                if (!ind.Validate(v)) goto restart;
                cur = child; v = cv;
            }
            return (Leaf)cur;
        restart:;
        }
    }

    // First live entry at or after `start`, walking forward (skips concurrently-emptied leaves).
    private bool TryFirstOfChain(Leaf? cur, out KeyValuePair<TKey, TValue> e)
    {
        while (cur != null)
        {
            if (!cur.TryReadVersion(out long v)) continue;
            int cnt = Volatile.Read(ref cur.Count);
            if (cnt > 0)
            {
                var kv = new KeyValuePair<TKey, TValue>(cur.Keys[0], cur.Values[0]);
                if (!cur.Validate(v)) continue;
                e = kv; return true;
            }
            Leaf? nx = cur.Next;
            if (!cur.Validate(v)) continue;
            cur = nx;
        }
        e = default; return false;
    }

    // Last live entry at or before `start`, walking backward via Prev.
    private bool TryLastOfChain(Leaf? cur, out KeyValuePair<TKey, TValue> e)
    {
        while (cur != null)
        {
            if (!cur.TryReadVersion(out long v)) continue;
            int cnt = Volatile.Read(ref cur.Count);
            if (cnt > 0)
            {
                var kv = new KeyValuePair<TKey, TValue>(cur.Keys[cnt - 1], cur.Values[cnt - 1]);
                if (!cur.Validate(v)) continue;
                e = kv; return true;
            }
            Leaf? pv = cur.Prev;
            if (!cur.Validate(v)) continue;
            cur = pv;
        }
        e = default; return false;
    }

    public bool TryGetFirst(out KeyValuePair<TKey, TValue> entry) => TryFirstOfChain(LeftmostLeaf(), out entry);
    public bool TryGetLast(out KeyValuePair<TKey, TValue> entry) => TryLastOfChain(RightmostLeaf(), out entry);

    /// <summary>Least entry with key ≥ <paramref name="key"/>. (ceilingEntry.)</summary>
    public bool TryGetCeiling(TKey key, out KeyValuePair<TKey, TValue> entry) => Relational(key, lower: false, inclusive: true, out entry);
    /// <summary>Least entry with key &gt; <paramref name="key"/>. (higherEntry.)</summary>
    public bool TryGetHigher(TKey key, out KeyValuePair<TKey, TValue> entry) => Relational(key, lower: false, inclusive: false, out entry);
    /// <summary>Greatest entry with key ≤ <paramref name="key"/>. (floorEntry.)</summary>
    public bool TryGetFloor(TKey key, out KeyValuePair<TKey, TValue> entry) => Relational(key, lower: true, inclusive: true, out entry);
    /// <summary>Greatest entry with key &lt; <paramref name="key"/>. (lowerEntry.)</summary>
    public bool TryGetLower(TKey key, out KeyValuePair<TKey, TValue> entry) => Relational(key, lower: true, inclusive: false, out entry);

    public bool TryGetCeilingKey(TKey key, out TKey k) => Proj(TryGetCeiling(key, out var e), e, out k);
    public bool TryGetHigherKey(TKey key, out TKey k) => Proj(TryGetHigher(key, out var e), e, out k);
    public bool TryGetFloorKey(TKey key, out TKey k) => Proj(TryGetFloor(key, out var e), e, out k);
    public bool TryGetLowerKey(TKey key, out TKey k) => Proj(TryGetLower(key, out var e), e, out k);
    private static bool Proj(bool ok, KeyValuePair<TKey, TValue> e, out TKey k) { k = ok ? e.Key : default!; return ok; }

    private bool Relational(TKey key, bool lower, bool inclusive, out KeyValuePair<TKey, TValue> entry)
    {
        if (key == null) throw new ArgumentNullException(nameof(key));
        for (; ; )
        {
            if (!TryDescendToLeaf(key, out var leaf, out long v)) continue;
            int cnt = Volatile.Read(ref leaf.Count);
            bool found;
            KeyValuePair<TKey, TValue> kv;
            if (!lower)   // ceiling / higher : first key >= key (or > key)
            {
                int i = inclusive ? LowerBound(leaf.Keys, cnt, key) : ChildIndex(leaf.Keys, cnt, key);
                if (i < cnt)
                {
                    kv = new KeyValuePair<TKey, TValue>(leaf.Keys[i], leaf.Values[i]);
                    if (!leaf.Validate(v)) continue;
                    found = true;
                }
                else
                {
                    Leaf? nx = leaf.Next;
                    if (!leaf.Validate(v)) continue;
                    found = TryFirstOfChain(nx, out kv);
                }
            }
            else          // floor / lower : last key <= key (or < key)
            {
                int i = (inclusive ? ChildIndex(leaf.Keys, cnt, key) : LowerBound(leaf.Keys, cnt, key)) - 1;
                if (i >= 0)
                {
                    kv = new KeyValuePair<TKey, TValue>(leaf.Keys[i], leaf.Values[i]);
                    if (!leaf.Validate(v)) continue;
                    found = true;
                }
                else
                {
                    Leaf? pv = leaf.Prev;
                    if (!leaf.Validate(v)) continue;
                    found = TryLastOfChain(pv, out kv);
                }
            }

            // Side-condition guard. An optimistic descent that races a concurrent split or
            // merge can land relative to the wrong leaf and yield a neighbor on the WRONG side
            // of `key` (e.g. lower(k) returning a key >= k). The relational contract is purely
            // ordinal, so re-check the side and retry on violation. This converges: under
            // quiescence the search is exact, so a retry eventually returns a validated result.
            if (found)
            {
                int c = _cmp.Compare(kv.Key, key);
                bool ok = lower ? (inclusive ? c <= 0 : c < 0)
                                : (inclusive ? c >= 0 : c > 0);
                if (!ok) continue;
            }
            entry = found ? kv : default;
            return found;
        }
    }

    /// <summary>Atomically removes and returns the smallest entry.</summary>
    public bool TryRemoveFirst(out KeyValuePair<TKey, TValue> entry)
    {
        for (; ; )
        {
            if (!TryGetFirst(out var f)) { entry = default; return false; }
            if (TryRemove(f.Key, out var v)) { entry = new KeyValuePair<TKey, TValue>(f.Key, v); return true; }
        }
    }

    /// <summary>Atomically removes and returns the largest entry.</summary>
    public bool TryRemoveLast(out KeyValuePair<TKey, TValue> entry)
    {
        for (; ; )
        {
            if (!TryGetLast(out var l)) { entry = default; return false; }
            if (TryRemove(l.Key, out var v)) { entry = new KeyValuePair<TKey, TValue>(l.Key, v); return true; }
        }
    }

    // =====================================================================
    //  Enumeration — weakly consistent, strictly ascending. Snapshots each leaf under an
    //  optimistic read and only yields keys strictly greater than the last yielded (so a key
    //  that a concurrent split made visible in two leaves is yielded at most once).
    // =====================================================================
    public IEnumerator<KeyValuePair<TKey, TValue>> GetEnumerator()
    {
        // leftmost leaf (optimistic descent)
        Leaf? leaf = LeftmostLeaf();
        bool have = false;
        TKey last = default!;
        var bufK = new TKey[_max + 1];
        var bufV = new TValue[_max + 1];

        while (leaf != null)
        {
            int n = SnapshotLeaf(leaf, bufK, bufV, out var next, out _);
            for (int i = 0; i < n; i++)
            {
                if (have && _cmp.Compare(bufK[i], last) <= 0) continue;   // dedup / enforce ascending
                last = bufK[i]; have = true;
                yield return new KeyValuePair<TKey, TValue>(bufK[i], bufV[i]);
            }
            leaf = next;                                                  // next read atomically with the snapshot
        }
    }

    private Leaf LeftmostLeaf()
    {
        for (; ; )
        {
            Node cur = _root;
            if (!cur.TryReadVersion(out long v)) continue;
            while (cur is Internal ind)
            {
                Node child = ind.Children[0];
                if (!ind.Validate(v)) goto restart;
                if (!child.TryReadVersion(out long cv)) goto restart;
                if (!ind.Validate(v)) goto restart;
                cur = child; v = cv;
            }
            return (Leaf)cur;
        restart:;
        }
    }

    // Snapshot a leaf's keys/values AND its chain links under a SINGLE validated version. Reading the
    // neighbour pointer in the same validated window is what makes scans merge-safe: a merge moves
    // right's keys into left and repoints left.Next past right, all under left's lock (one version bump).
    // If a scan snapshotted left's OLD keys it must NOT then follow left's NEW Next (it would skip
    // right's keys, which weren't in the old snapshot) — so we re-read Next/Prev inside the validated
    // block and retry on any version change, yielding a consistent (keys, links) pair.
    private int SnapshotLeaf(Leaf leaf, TKey[] bufK, TValue[] bufV, out Leaf? next, out Leaf? prev)
    {
        for (; ; )
        {
            if (!leaf.TryReadVersion(out long v)) continue;
            int n = Volatile.Read(ref leaf.Count);
            if (n > bufK.Length) n = bufK.Length;
            Array.Copy(leaf.Keys, bufK, n);
            Array.Copy(leaf.Values, bufV, n);
            next = Volatile.Read(ref leaf.Next);
            prev = Volatile.Read(ref leaf.Prev);
            if (leaf.Validate(v)) return n;   // Validate carries the LoadLoad fence (seqlock read)
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    // ---- snapshots (ascending), like ConcurrentDictionary ----
    public ICollection<TKey> Keys { get { var l = new List<TKey>(); foreach (var kv in this) l.Add(kv.Key); return l; } }
    public ICollection<TValue> Values { get { var l = new List<TValue>(); foreach (var kv in this) l.Add(kv.Value); return l; } }
    IEnumerable<TKey> IReadOnlyDictionary<TKey, TValue>.Keys => Keys;
    IEnumerable<TValue> IReadOnlyDictionary<TKey, TValue>.Values => Values;
    public IEnumerable<TKey> DescendingKeys { get { foreach (var kv in Reverse()) yield return kv.Key; } }

    // =====================================================================
    //  Clear, conveniences, functional helpers (ConcurrentDictionary parity)
    // =====================================================================

    /// <summary>Resets to empty by swapping in a fresh leaf root. Quiescent semantics under contention.</summary>
    public void Clear()
    {
        _root = NewLeaf();
        _count.Set(0);
    }

    public IComparer<TKey> Comparer => _cmp;

    public bool ContainsValue(TValue value)
    {
        var cmp = EqualityComparer<TValue>.Default;
        foreach (var kv in this) if (cmp.Equals(kv.Value, value)) return true;
        return false;
    }

    public TValue GetValueOrDefault(TKey key, TValue defaultValue) => TryGetValue(key, out var v) ? v : defaultValue;
    public TValue? GetValueOrDefault(TKey key) => TryGetValue(key, out var v) ? v : default;

    public bool TryReplace(TKey key, TValue newValue, out TValue previous)
    {
        for (; ; )
        {
            if (!TryGetValue(key, out var cur)) { previous = default!; return false; }
            if (DoReplace(key, newValue, true, cur)) { previous = cur; return true; }
        }
    }

    public TValue GetOrAdd(TKey key, TValue value)
    {
        for (; ; )
        {
            if (TryGetValue(key, out var v)) return v;
            if (TryAdd(key, value)) return value;
        }
    }

    public TValue GetOrAdd(TKey key, Func<TKey, TValue> valueFactory)
    {
        for (; ; )
        {
            if (TryGetValue(key, out var v)) return v;
            var nv = valueFactory(key);
            if (TryAdd(key, nv)) return nv;
        }
    }


    public bool ComputeIfPresent(TKey key, Func<TKey, TValue, TValue> remappingFunction, out TValue newValue)
    {
        for (; ; )
        {
            if (!TryGetValue(key, out var cur)) { newValue = default!; return false; }
            var nv = remappingFunction(key, cur);
            if (DoReplace(key, nv, true, cur)) { newValue = nv; return true; }
        }
    }

    public TValue AddOrUpdate(TKey key, TValue addValue, Func<TKey, TValue, TValue> updateValueFactory)
    {
        for (; ; )
        {
            if (TryGetValue(key, out var cur))
            {
                var nv = updateValueFactory(key, cur);
                if (DoReplace(key, nv, true, cur)) return nv;
            }
            else if (TryAdd(key, addValue)) return addValue;
        }
    }

    public TValue AddOrUpdate(TKey key, Func<TKey, TValue> addValueFactory, Func<TKey, TValue, TValue> updateValueFactory)
    {
        for (; ; )
        {
            if (TryGetValue(key, out var cur))
            {
                var nv = updateValueFactory(key, cur);
                if (DoReplace(key, nv, true, cur)) return nv;
            }
            else { var add = addValueFactory(key); if (TryAdd(key, add)) return add; }
        }
    }

    public void AddRange(IEnumerable<KeyValuePair<TKey, TValue>> items)
    {
        foreach (var kv in items) this[kv.Key] = kv.Value;
    }

    public void ReplaceAll(Func<TKey, TValue, TValue> transform)
    {
        foreach (var kv in this)
            for (; ; )
            {
                if (!TryGetValue(kv.Key, out var cur)) break;
                if (DoReplace(kv.Key, transform(kv.Key, cur), true, cur)) break;
            }
    }

    // =====================================================================
    //  IDictionary<TKey,TValue> / ICollection<KeyValuePair<,>>
    // =====================================================================
    public void Add(TKey key, TValue value)
    {
        if (!TryAdd(key, value)) throw new ArgumentException($"An item with the same key already exists. Key: {key}", nameof(key));
    }

    public bool Remove(TKey key) => TryRemove(key, out _);
    public bool IsReadOnly => false;
    void ICollection<KeyValuePair<TKey, TValue>>.Add(KeyValuePair<TKey, TValue> item) => Add(item.Key, item.Value);
    public bool Contains(KeyValuePair<TKey, TValue> item)
        => TryGetValue(item.Key, out var v) && EqualityComparer<TValue>.Default.Equals(v, item.Value);
    public bool Remove(KeyValuePair<TKey, TValue> item) => TryRemove(item);
    public void CopyTo(KeyValuePair<TKey, TValue>[] array, int arrayIndex)
    {
        ArgumentNullException.ThrowIfNull(array);
        if (arrayIndex < 0 || arrayIndex > array.Length) throw new ArgumentOutOfRangeException(nameof(arrayIndex));
        foreach (var kv in this)
        {
            if (arrayIndex >= array.Length) throw new ArgumentException("Destination array is not long enough.");
            array[arrayIndex++] = kv;
        }
    }

    // =====================================================================
    //  Native bounded range scans (the B+-tree's strength): walk the leaf chain
    //  forward (or backward via Prev), snapshotting each leaf, filtering to the
    //  bounds, deduping to stay strictly monotone under concurrent splits.
    // =====================================================================
    private Leaf LeafForKey(TKey key) { for (; ; ) if (TryDescendToLeaf(key, out var leaf, out _)) return leaf; }

    private IEnumerable<KeyValuePair<TKey, TValue>> ScanAscending(bool hasLo, TKey lo, bool loInc, bool hasHi, TKey hi, bool hiInc)
    {
        Leaf? leaf = hasLo ? LeafForKey(lo) : LeftmostLeaf();
        bool have = false; TKey last = default!;
        var bufK = new TKey[_max + 1]; var bufV = new TValue[_max + 1];
        while (leaf != null)
        {
            int n = SnapshotLeaf(leaf, bufK, bufV, out var next, out _);
            for (int i = 0; i < n; i++)
            {
                TKey k = bufK[i];
                if (hasLo) { int c = _cmp.Compare(k, lo); if (c < 0 || (c == 0 && !loInc)) continue; }
                if (hasHi) { int c = _cmp.Compare(k, hi); if (c > 0 || (c == 0 && !hiInc)) yield break; }
                if (have && _cmp.Compare(k, last) <= 0) continue;
                last = k; have = true;
                yield return new KeyValuePair<TKey, TValue>(k, bufV[i]);
            }
            leaf = next;                                                  // atomic with the snapshot
        }
    }

    private IEnumerable<KeyValuePair<TKey, TValue>> ScanDescending(bool hasLo, TKey lo, bool loInc, bool hasHi, TKey hi, bool hiInc)
    {
        Leaf? leaf = hasHi ? LeafForKey(hi) : RightmostLeaf();
        bool have = false; TKey last = default!;
        var bufK = new TKey[_max + 1]; var bufV = new TValue[_max + 1];
        while (leaf != null)
        {
            int n = SnapshotLeaf(leaf, bufK, bufV, out _, out var prev);
            for (int i = n - 1; i >= 0; i--)
            {
                TKey k = bufK[i];
                if (hasHi) { int c = _cmp.Compare(k, hi); if (c > 0 || (c == 0 && !hiInc)) continue; }
                if (hasLo) { int c = _cmp.Compare(k, lo); if (c < 0 || (c == 0 && !loInc)) yield break; }
                if (have && _cmp.Compare(k, last) >= 0) continue;
                last = k; have = true;
                yield return new KeyValuePair<TKey, TValue>(k, bufV[i]);
            }
            leaf = prev;                                                  // atomic with the snapshot
        }
    }

    // =====================================================================
    //  NavigableMap views
    // =====================================================================
    public RangeView GetViewBetween(TKey fromKey, TKey toKey) => GetViewBetween(fromKey, true, toKey, false);
    public RangeView GetViewBetween(TKey fromKey, bool fromInclusive, TKey toKey, bool toInclusive)
        => new(this, true, fromKey, fromInclusive, true, toKey, toInclusive, descending: false);
    public RangeView GetViewTo(TKey toKey, bool inclusive = false)
        => new(this, false, default!, false, true, toKey, inclusive, descending: false);
    public RangeView GetViewFrom(TKey fromKey, bool inclusive = true)
        => new(this, true, fromKey, inclusive, false, default!, false, descending: false);
    public RangeView Reverse()
        => new(this, false, default!, false, false, default!, false, descending: true);

    /// <summary>A live, navigable view over a key range (and/or reversed). Reads/scans use the native
    /// leaf chain; writes are bounds-checked and delegate to the parent. Weakly consistent, like the parent.</summary>
    public sealed class RangeView : IDictionary<TKey, TValue>, IReadOnlyDictionary<TKey, TValue>
    {
        private readonly ConcurrentBTreeDictionary<TKey, TValue> _p;
        private readonly bool _hasLo, _loInc, _hasHi, _hiInc, _desc;
        private readonly TKey _lo, _hi;

        internal RangeView(ConcurrentBTreeDictionary<TKey, TValue> p, bool hasLo, TKey lo, bool loInc, bool hasHi, TKey hi, bool hiInc, bool descending)
        { _p = p; _hasLo = hasLo; _lo = lo; _loInc = loInc; _hasHi = hasHi; _hi = hi; _hiInc = hiInc; _desc = descending; }

        private bool TooLow(TKey k) { if (!_hasLo) return false; int c = _p._cmp.Compare(k, _lo); return c < 0 || (c == 0 && !_loInc); }
        private bool TooHigh(TKey k) { if (!_hasHi) return false; int c = _p._cmp.Compare(k, _hi); return c > 0 || (c == 0 && !_hiInc); }
        private bool InRange(TKey k) => !TooLow(k) && !TooHigh(k);
        private void CheckRange(TKey k) { if (!InRange(k)) throw new ArgumentOutOfRangeException(nameof(k), $"Key {k} is outside the sub-dictionary range."); }

        public IEnumerator<KeyValuePair<TKey, TValue>> GetEnumerator()
            => (_desc ? _p.ScanDescending(_hasLo, _lo, _loInc, _hasHi, _hi, _hiInc)
                      : _p.ScanAscending(_hasLo, _lo, _loInc, _hasHi, _hi, _hiInc)).GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        // ascending-order first/last in range
        private bool AscFirst(out KeyValuePair<TKey, TValue> e)
        {
            bool ok = _hasLo ? (_loInc ? _p.TryGetCeiling(_lo, out e) : _p.TryGetHigher(_lo, out e)) : _p.TryGetFirst(out e);
            if (ok && TooHigh(e.Key)) { e = default; return false; }
            return ok;
        }
        private bool AscLast(out KeyValuePair<TKey, TValue> e)
        {
            bool ok = _hasHi ? (_hiInc ? _p.TryGetFloor(_hi, out e) : _p.TryGetLower(_hi, out e)) : _p.TryGetLast(out e);
            if (ok && TooLow(e.Key)) { e = default; return false; }
            return ok;
        }
        private bool RangeCeiling(TKey key, out KeyValuePair<TKey, TValue> e)
        { if (!_p.TryGetCeiling(key, out var c)) { e = default; return false; } if (TooLow(c.Key)) return AscFirst(out e); if (TooHigh(c.Key)) { e = default; return false; } e = c; return true; }
        private bool RangeHigher(TKey key, out KeyValuePair<TKey, TValue> e)
        { if (!_p.TryGetHigher(key, out var c)) { e = default; return false; } if (TooLow(c.Key)) return AscFirst(out e); if (TooHigh(c.Key)) { e = default; return false; } e = c; return true; }
        private bool RangeFloor(TKey key, out KeyValuePair<TKey, TValue> e)
        { if (!_p.TryGetFloor(key, out var c)) { e = default; return false; } if (TooHigh(c.Key)) return AscLast(out e); if (TooLow(c.Key)) { e = default; return false; } e = c; return true; }
        private bool RangeLower(TKey key, out KeyValuePair<TKey, TValue> e)
        { if (!_p.TryGetLower(key, out var c)) { e = default; return false; } if (TooHigh(c.Key)) return AscLast(out e); if (TooLow(c.Key)) { e = default; return false; } e = c; return true; }

        // view-order navigable (inverted when descending)
        public bool TryGetFirst(out KeyValuePair<TKey, TValue> e) => _desc ? AscLast(out e) : AscFirst(out e);
        public bool TryGetLast(out KeyValuePair<TKey, TValue> e) => _desc ? AscFirst(out e) : AscLast(out e);
        public bool TryGetCeiling(TKey key, out KeyValuePair<TKey, TValue> e) => _desc ? RangeFloor(key, out e) : RangeCeiling(key, out e);
        public bool TryGetHigher(TKey key, out KeyValuePair<TKey, TValue> e) => _desc ? RangeLower(key, out e) : RangeHigher(key, out e);
        public bool TryGetFloor(TKey key, out KeyValuePair<TKey, TValue> e) => _desc ? RangeCeiling(key, out e) : RangeFloor(key, out e);
        public bool TryGetLower(TKey key, out KeyValuePair<TKey, TValue> e) => _desc ? RangeHigher(key, out e) : RangeLower(key, out e);

        public bool ContainsKey(TKey key) => InRange(key) && _p.ContainsKey(key);
        public bool TryGetValue(TKey key, out TValue value) { if (InRange(key)) return _p.TryGetValue(key, out value); value = default!; return false; }
        public bool IsEmpty { get { using var e = GetEnumerator(); return !e.MoveNext(); } }
        public int Count { get { int n = 0; foreach (var _ in this) n++; return n; } }
        public bool IsReadOnly => false;

        public ICollection<TKey> Keys { get { var l = new List<TKey>(); foreach (var kv in this) l.Add(kv.Key); return l; } }
        public ICollection<TValue> Values { get { var l = new List<TValue>(); foreach (var kv in this) l.Add(kv.Value); return l; } }
        IEnumerable<TKey> IReadOnlyDictionary<TKey, TValue>.Keys => Keys;
        IEnumerable<TValue> IReadOnlyDictionary<TKey, TValue>.Values => Values;

        public TValue this[TKey key]
        {
            get => InRange(key) && _p.TryGetValue(key, out var v) ? v : throw new KeyNotFoundException();
            set { CheckRange(key); _p[key] = value; }
        }
        public void Add(TKey key, TValue value) { CheckRange(key); _p.Add(key, value); }
        public bool TryAdd(TKey key, TValue value) { CheckRange(key); return _p.TryAdd(key, value); }
        public bool Remove(TKey key) => InRange(key) && _p.Remove(key);
        public void Clear() { foreach (var k in Keys) _p.Remove(k); }
        void ICollection<KeyValuePair<TKey, TValue>>.Add(KeyValuePair<TKey, TValue> item) => Add(item.Key, item.Value);
        public bool Contains(KeyValuePair<TKey, TValue> item) => InRange(item.Key) && _p.Contains(item);
        public bool Remove(KeyValuePair<TKey, TValue> item) => InRange(item.Key) && _p.Remove(item);
        public void CopyTo(KeyValuePair<TKey, TValue>[] array, int arrayIndex)
        {
            ArgumentNullException.ThrowIfNull(array);
            foreach (var kv in this) { if (arrayIndex >= array.Length) throw new ArgumentException("Destination array is not long enough."); array[arrayIndex++] = kv; }
        }
        public RangeView Reverse() => new(_p, _hasLo, _lo, _loInc, _hasHi, _hi, _hiInc, !_desc);
    }

    /// <summary>Test hook: total keys actually present in the leaf chain (quiescent only).</summary>
    public long CountLeafKeys()
    {
        long sum = 0;
        Node cur = _root;
        while (cur is Internal ind) cur = ind.Children[0];
        for (var leaf = (Leaf)cur; leaf != null; leaf = leaf.Next) sum += leaf.Count;
        return sum;
    }

    /// <summary>
    /// Quiescent structural check (call with no concurrent ops). Verifies the B+-tree invariants:
    /// (1) BALANCE — every leaf is at the same depth; (2) keys strictly ascending within each node
    /// and inside their separator window; (3) the leaf sibling chain is globally strictly ascending
    /// and complete; (4) leaf-key total == Count. Note: it intentionally does NOT check minimum
    /// occupancy — lazy delete (no merge) is allowed to leave underfull/empty nodes.
    /// </summary>
    public void Validate()
    {
        int leafDepth = -1, leafTotal = 0;
        ValidateNode(_root, 0, false, default!, false, default!, ref leafDepth, ref leafTotal);
        long cnt = _count.Sum();
        if (leafTotal != cnt) throw new InvalidOperationException($"Count mismatch: tree holds {leafTotal}, _count={cnt}");

        Node n = _root;
        while (n is Internal ind) n = ind.Children[0];
        bool first = true; TKey prev = default!; long chain = 0;
        for (var leaf = (Leaf)n; leaf != null; leaf = leaf.Next)
            for (int i = 0; i < leaf.Count; i++)
            {
                if (!first && _cmp.Compare(leaf.Keys[i], prev) <= 0)
                    throw new InvalidOperationException($"leaf chain not strictly ascending at {leaf.Keys[i]} after {prev}");
                prev = leaf.Keys[i]; first = false; chain++;
            }
        if (chain != cnt) throw new InvalidOperationException($"leaf chain has {chain} keys, _count={cnt}");
    }

    private void ValidateNode(Node node, int depth, bool hasLo, TKey lo, bool hasHi, TKey hi, ref int leafDepth, ref int leafTotal)
    {
        for (int i = 0; i < node.Count; i++)
        {
            if (i > 0 && _cmp.Compare(node.Keys[i - 1], node.Keys[i]) >= 0)
                throw new InvalidOperationException("keys not ascending within node");
            if (hasLo && _cmp.Compare(node.Keys[i], lo) < 0) throw new InvalidOperationException("key below separator lower bound");
            if (hasHi && _cmp.Compare(node.Keys[i], hi) >= 0) throw new InvalidOperationException("key at/above separator upper bound");
        }
        if (node is Leaf leaf)
        {
            if (leafDepth == -1) leafDepth = depth;
            else if (depth != leafDepth) throw new InvalidOperationException($"UNBALANCED: leaf at depth {depth}, expected {leafDepth}");
            leafTotal += leaf.Count;
            return;
        }
        var ind = (Internal)node;
        for (int i = 0; i <= ind.Count; i++)
        {
            bool cLo = i > 0 || hasLo; TKey childLo = i > 0 ? ind.Keys[i - 1] : lo;
            bool cHi = i < ind.Count || hasHi; TKey childHi = i < ind.Count ? ind.Keys[i] : hi;
            ValidateNode(ind.Children[i], depth + 1, cLo, childLo, cHi, childHi, ref leafDepth, ref leafTotal);
        }
    }

    /// <summary>Test/diagnostic hook (quiescent): (depth, internalNodes, leaves, avgLeafFillPercent).</summary>
    public (int Depth, int Internals, int Leaves, double AvgLeafFill) DebugStats()
    {
        int depth = 0; Node n = _root;
        while (n is Internal ind) { depth++; n = ind.Children[0]; }
        int internals = 0, leaves = 0; long keys = 0;
        CountNodes(_root, ref internals, ref leaves, ref keys);
        double fill = leaves == 0 ? 0 : 100.0 * keys / ((double)leaves * _max);
        return (depth, internals, leaves, fill);
    }

    private void CountNodes(Node node, ref int internals, ref int leaves, ref long keys)
    {
        if (node is Leaf leaf) { leaves++; keys += leaf.Count; return; }
        var ind = (Internal)node;
        internals++;
        for (int i = 0; i <= ind.Count; i++) CountNodes(ind.Children[i], ref internals, ref leaves, ref keys);
    }
}
