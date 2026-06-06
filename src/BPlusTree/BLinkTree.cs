using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Mobratil.Collections;

/// <summary>
/// EXPERIMENT — a Lehman &amp; Yao B-link tree, to compare write-scalability against the OLC
/// <see cref="ConcurrentBTreeDictionary{TKey,TValue}"/>.
///
/// The B-link idea: every node carries a <c>HighKey</c> (exclusive upper bound of the keys it owns)
/// and a <c>Right</c> link to its same-level right sibling. A split is a local "half split": the node
/// is locked, its upper half is moved into a fresh right sibling, the node's HighKey is lowered to the
/// split key and its Right link points at the new sibling — all under the node's own lock. The
/// separator is pushed into the parent as a SEPARATE, independently-locked step (re-descending to find
/// the parent). So a writer never holds locks across more than one level at a time — that's the
/// scalability bet versus OLC's latch-coupling.
///
/// Correctness rests on the move-right rule: any descent (read or write) that lands on a node whose
/// HighKey is already &lt;= the search key simply follows the Right link instead — because the right-link
/// chain at every level is complete, this self-corrects for a parent whose separator hasn't been
/// installed yet. Reads are lock-free and version-validated (to reject torn reads); writers take the
/// node's write lock and re-check the move-right condition under it.
///
/// Scope of the experiment: TryAdd / TryGetValue / indexer / TryRemove (LAZY delete — no merge yet) /
/// forward enumeration / Validate. Enough to verify correctness and benchmark write scaling. Sibling
/// merge on a B-link tree is the known-hard part (right-links can dangle to removed nodes) and is only
/// worth building if this wins.
/// </summary>
public sealed class BLinkTree<TKey, TValue> : IDictionary<TKey, TValue>, IReadOnlyDictionary<TKey, TValue>
{
    private abstract class Node
    {
        internal long Version;          // even = unlocked, odd = write-locked
        internal int Level;             // leaves = 0; internal = childLevel + 1
        internal int Count;             // keys in use
        internal TKey[] Keys = default!;
        internal TKey HighKey = default!;   // exclusive upper bound of this node's range
        internal bool HasHighKey;           // false on the rightmost node of each level (covers +inf)
        internal Node? Right;               // right-link to the same-level sibling
        internal volatile bool Dead;        // half-dead: merged away, kept alive for forwarding until GC reclaims it

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
        internal void WriteUnlock() => Volatile.Write(ref Version, Volatile.Read(ref Version) + 1);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal bool TryReadVersion(out long v) { v = Volatile.Read(ref Version); return (v & 1L) == 0; }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal bool Validate(long v) => Volatile.Read(ref Version) == v;
    }

    private sealed class Leaf : Node { internal TValue[] Values = default!; internal Leaf? Prev; }   // Prev: backward link for descending scans (lazy delete -> never removed, so never dangles)
    private sealed class Internal : Node { internal Node[] Children = default!; }

    private readonly IComparer<TKey> _cmp;
    private readonly int _max;
    private readonly int _min;              // online-merge trigger: a node below this is "underfull"
    private volatile Node _root;
    private readonly object _growLock = new();
    private readonly StripedCounter _count = new();             // LongAdder-style: no single-cache-line write hotspot
    private readonly StripedCounter _deletedSinceCompact = new();   // lazy-delete tombstone pressure (only relevant if online merge is off)
    private bool _onlineMerge = false;

    /// <summary>EXPERIMENTAL — off by default. When on, an underfull delete triggers an online half-dead
    /// sibling merge so memory tracks the live set automatically (no manual Compact()). Correct
    /// sequentially and under DISJOINT-key concurrency. Under heavy OVERLAPPING-key contention it had
    /// multiple subtle races: one was root-caused and fixed (the "fully-linked" gate below — never merge a
    /// node mid-split, which used to leave a stale parent separator that misrouted inserts), but at least
    /// one structural-drift race remains. Concurrent merge on a B-link tree is genuinely hard (Lehman-Yao
    /// omitted deletion; Postgres's took years to harden) and is NOT considered correct here. For
    /// guaranteed reclamation leave this off and use <see cref="Compact"/>; for automatic online
    /// reclamation use the OLC <c>ConcurrentBTreeDictionary</c>. Kept as a documented record of the attempt.</summary>
    public bool EnableOnlineMerge { get => _onlineMerge; set => _onlineMerge = value; }

    public BLinkTree(int order = 64, IComparer<TKey>? comparer = null)
    {
        if (order < 3) throw new ArgumentOutOfRangeException(nameof(order), "order must be >= 3");
        _max = order;
        _min = order / 2;
        _cmp = comparer ?? Comparer<TKey>.Default;
        _root = NewLeaf();
    }

    // Sum() is a weakly-consistent striped read (LongAdder semantics) -> can momentarily be < 0 mid-churn; clamp.
    public int Count { get { long c = _count.Sum(); return c <= 0 ? 0 : c >= int.MaxValue ? int.MaxValue : (int)c; } }
    public bool IsEmpty => _count.Sum() <= 0;
    public IComparer<TKey> Comparer => _cmp;

    private Leaf NewLeaf() => new() { Keys = new TKey[_max + 1], Values = new TValue[_max + 1], Level = 0 };
    private Internal NewInternal() => new() { Keys = new TKey[_max + 1], Children = new Node[_max + 2] };

    // index of the first key >= `key` is ~return for Find; ChildIndex gives the child to descend into.
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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool MoveRight(Node n, TKey key) => n.HasHighKey && _cmp.Compare(key, n.HighKey) >= 0;

    // =====================================================================
    //  Lookup — lock-free, version-validated, with move-right.
    // =====================================================================
    public bool TryGetValue(TKey key, out TValue value)
    {
        if (key == null) throw new ArgumentNullException(nameof(key));
        Node node = _root;
        var sw = new SpinWait();
        for (; ; )
        {
            if (!node.TryReadVersion(out long v)) { sw.SpinOnce(); continue; }   // locked -> spin on this node

            if (node is Internal ind)
            {
                Node next = MoveRight(ind, key) ? ind.Right! : ind.Children[ChildIndex(ind.Keys, ind.Count, key)];
                if (!ind.Validate(v)) continue;                  // torn read -> re-read this node
                node = next; sw = new SpinWait();
                continue;
            }

            var leaf = (Leaf)node;
            if (MoveRight(leaf, key))
            {
                Node right = leaf.Right!;
                if (!leaf.Validate(v)) continue;
                node = right; sw = new SpinWait();
                continue;
            }
            int i = Find(leaf.Keys, leaf.Count, key);
            bool found = i >= 0;
            TValue val = found ? leaf.Values[i] : default!;
            if (!leaf.Validate(v)) continue;
            value = val;
            return found;
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
    //  Insert — descend (validated, move-right), lock the leaf, split locally, push the
    //  separator to the parent as a separate re-descended step.
    // =====================================================================

    // Validated lock-free descent to the node at `stopLevel` whose range contains `key` (caller will
    // re-check move-right under the lock). Validation rejects torn reads (which could overshoot right);
    // legitimate staleness is corrected by move-right under the lock.
    private Node DescendTo(TKey key, int stopLevel)
    {
        Node node = _root;
        var sw = new SpinWait();
        for (; ; )
        {
            if (node.Level == stopLevel) return node;
            if (!node.TryReadVersion(out long v)) { sw.SpinOnce(); continue; }
            var ind = (Internal)node;
            Node next = MoveRight(ind, key) ? ind.Right! : ind.Children[ChildIndex(ind.Keys, ind.Count, key)];
            if (!ind.Validate(v)) continue;
            node = next; sw = new SpinWait();
        }
    }

    // Descend, lock the leaf, move-right under the lock, AND skip a half-dead leaf (merged away): re-descend
    // so we land on the live node that absorbed it. Returns a locked, live leaf that owns `key`'s range.
    private Leaf LockLeafForWrite(TKey key)
    {
        for (; ; )
        {
            var leaf = (Leaf)DescendTo(key, 0);
            leaf.WriteLock();
            while (MoveRight(leaf, key)) { var r = (Leaf)leaf.Right!; leaf.WriteUnlock(); r.WriteLock(); leaf = r; }
            if (leaf.Dead) { leaf.WriteUnlock(); continue; }   // merged away while we descended -> retry
            return leaf;
        }
    }

    private static void RemoveChild(Internal n, int ci)
    {
        int c = n.Count;
        int sep = ci > 0 ? ci - 1 : 0;
        Array.Copy(n.Keys, sep + 1, n.Keys, sep, c - 1 - sep);
        Array.Copy(n.Children, ci + 1, n.Children, ci, c - ci);
        n.Count = c - 1;
        Array.Clear(n.Keys, c - 1, 1);
        Array.Clear(n.Children, c, 1);
    }

    // =====================================================================
    //  Online concurrent merge (half-dead protocol) — automatic, pause-free reclamation.
    //
    //  When a delete leaves a node underfull we merge its RIGHT sibling R into it (so we never have to
    //  hunt for a left sibling). We briefly latch ONLY the parent P + the pair X,R (not a chain), and
    //  only when X.Right == R == P.Children[ci+1] — i.e. they're the same parent's adjacent, fully-linked
    //  children with no pending split between them. That single gate sidesteps B-link's parent-staleness.
    //
    //  Half-dead R: after X absorbs R, R keeps its keys + Right-link + Prev intact and is flagged Dead.
    //  A reader already on R finds its key there or moves right; a writer that locks R sees Dead and
    //  re-descends (P no longer routes to R). X.HighKey/Right/Count are published atomically under X's
    //  lock, so a version-validating reader sees all-old (-> move right to the still-live R) or all-new
    //  (-> find in X) — never a missed key. GC reclaims R once no thread references it (the .NET win).
    //
    //  Propagates up: removing R drops a child from P; if P underfulls we merge P with its right sibling
    //  the same way, up to a single-child root collapse.
    // =====================================================================
    private void MergeOnline(TKey key)
    {
        int childLevel = 0;
        for (; ; )
        {
            if (_root.Level <= childLevel) return;             // the underfull node is (under) the root -> done
            // re-descend to the parent level, lock it, move-right + skip a dead parent
            Internal P;
            for (; ; )
            {
                P = (Internal)DescendTo(key, childLevel + 1);
                P.WriteLock();
                while (MoveRight(P, key)) { var pr = (Internal)P.Right!; P.WriteUnlock(); pr.WriteLock(); P = pr; }
                if (!P.Dead) break;
                P.WriteUnlock();
            }

            int ci = ChildIndex(P.Keys, P.Count, key);
            if (ci >= P.Count) { P.WriteUnlock(); return; }    // X is P's rightmost child -> no same-parent right sibling
            Node X = P.Children[ci], R = P.Children[ci + 1];

            X.WriteLock();
            // X must still be underfull, alive, and adjacent to R with no pending split (X.Right == R)
            if (X.Dead || X.Count >= _min || !ReferenceEquals(X.Right, R))
            { X.WriteUnlock(); P.WriteUnlock(); return; }
            R.WriteLock();
            int combined = X is Leaf ? X.Count + R.Count : X.Count + 1 + R.Count;
            // FULLY-LINKED gate (the real fix): only merge X and R when BOTH match the parent's separators
            // exactly — i.e. neither has a pending split whose separator hasn't been posted yet. If we
            // merged an R mid-split, X would inherit R's lowered HighKey while the parent kept R's old
            // high separator, leaving a window where an insert routes a key into the wrong leaf. Requiring
            // X.HighKey == P.Keys[ci] and R.HighKey == P.Keys[ci+1] (stable: P, X, R all locked) closes it.
            bool linked = X.HasHighKey && _cmp.Compare(X.HighKey, P.Keys[ci]) == 0;
            if (linked)
            {
                if (ci + 1 < P.Count) linked = R.HasHighKey && _cmp.Compare(R.HighKey, P.Keys[ci + 1]) == 0;
                else linked = R.HasHighKey == P.HasHighKey && (!R.HasHighKey || _cmp.Compare(R.HighKey, P.HighKey) == 0);
            }
            if (!linked) { R.WriteUnlock(); X.WriteUnlock(); P.WriteUnlock(); return; }
            // Self-validating gate: only commit a merge whose result is provably key-ordered. Under heavy
            // shared-key split/merge collision the X.Right==R link can momentarily be ahead of the
            // ordering invariant; rather than reason about every interleaving, we refuse any merge that
            // would misorder keys and let a later delete retry. This keeps the structure always-sorted.
            bool safe;
            if (X is Leaf xlc)
            {
                var rlc = (Leaf)R;
                safe = xlc.Count == 0 || rlc.Count == 0 || _cmp.Compare(xlc.Keys[xlc.Count - 1], rlc.Keys[0]) < 0;
            }
            else
            {
                var xic = (Internal)X; var ric = (Internal)R; TKey sep = P.Keys[ci];
                safe = (xic.Count == 0 || _cmp.Compare(xic.Keys[xic.Count - 1], sep) < 0)
                    && (ric.Count == 0 || _cmp.Compare(sep, ric.Keys[0]) < 0);
            }
            if (R.Dead || combined > _max || !safe)
            { R.WriteUnlock(); X.WriteUnlock(); P.WriteUnlock(); return; }

            if (X is Leaf xl)
            {
                var rl = (Leaf)R;
                var rRight = (Leaf?)rl.Right;
                if (rRight != null) rRight.WriteLock();
                int lc = xl.Count, rc = rl.Count;
                Array.Copy(rl.Keys, 0, xl.Keys, lc, rc);
                Array.Copy(rl.Values, 0, xl.Values, lc, rc);
                xl.HighKey = rl.HighKey; xl.HasHighKey = rl.HasHighKey;
                xl.Right = rRight;
                xl.Count = lc + rc;                            // publish count last
                if (rRight != null) { rRight.Prev = xl; rRight.WriteUnlock(); }
                rl.Dead = true;                                // keys/Right/Prev kept intact for forwarding
            }
            else
            {
                var xi = (Internal)X; var ri = (Internal)R;
                int lc = xi.Count, rc = ri.Count;
                xi.Keys[lc] = P.Keys[ci];                      // pull the separator down between the halves
                Array.Copy(ri.Keys, 0, xi.Keys, lc + 1, rc);
                Array.Copy(ri.Children, 0, xi.Children, lc + 1, rc + 1);
                xi.HighKey = ri.HighKey; xi.HasHighKey = ri.HasHighKey;
                xi.Right = ri.Right;
                xi.Count = lc + 1 + rc;
                ri.Dead = true;                                // children stay shared with X -> descents forward correctly
            }

            RemoveChild(P, ci + 1);                            // drop R's separator + child from the parent
            bool collapseRoot = ReferenceEquals(P, _root) && P.Count == 0;
            Node? newRoot = collapseRoot ? P.Children[0] : null;
            bool pUnderfull = !collapseRoot && !ReferenceEquals(P, _root) && P.Count < _min;
            int pLevel = P.Level;

            R.WriteUnlock();
            X.WriteUnlock();
            if (collapseRoot) _root = newRoot!;               // publish new root while holding old root's lock
            P.WriteUnlock();

            if (!pUnderfull) return;
            childLevel = pLevel;                              // P lost a child and is underfull -> merge P up
        }
    }

    private bool Insert(TKey key, TValue value, bool onlyIfAbsent)
    {
        if (key == null) throw new ArgumentNullException(nameof(key));
        var leaf = LockLeafForWrite(key);

        int idx = Find(leaf.Keys, leaf.Count, key);
        if (idx >= 0)
        {
            if (!onlyIfAbsent) leaf.Values[idx] = value;
            leaf.WriteUnlock();
            return false;
        }

        InsertIntoLeaf(leaf, ~idx, key, value);
        _count.Increment();
        if (leaf.Count <= _max)
        {
            leaf.WriteUnlock();
            return true;
        }

        // overfull -> half split, then publish the separator to the parent
        var newRight = HalfSplitLeaf(leaf, out TKey sepKey);
        leaf.WriteUnlock();
        InsertSeparator(sepKey, newRight, childLevel: 0);
        return true;
    }

    private static void InsertIntoLeaf(Leaf leaf, int at, TKey key, TValue value)
    {
        Array.Copy(leaf.Keys, at, leaf.Keys, at + 1, leaf.Count - at);
        Array.Copy(leaf.Values, at, leaf.Values, at + 1, leaf.Count - at);
        leaf.Keys[at] = key;
        leaf.Values[at] = value;
        leaf.Count++;
    }

    private static void InsertIntoInternal(Internal n, int ci, TKey sep, Node right)
    {
        Array.Copy(n.Keys, ci, n.Keys, ci + 1, n.Count - ci);
        Array.Copy(n.Children, ci + 1, n.Children, ci + 2, n.Count - ci);
        n.Keys[ci] = sep;
        n.Children[ci + 1] = right;
        n.Count++;
    }

    // Half-split a locked, overfull leaf: move the upper half into a new right sibling, hand the new
    // sibling the old HighKey/Right, and point this leaf's HighKey/Right at the split. Returns the new
    // sibling; `sepKey` (its first key) is the separator to install in the parent.
    private Leaf HalfSplitLeaf(Leaf leaf, out TKey sepKey)
    {
        int total = leaf.Count;            // _max + 1
        int mid = total / 2;
        int rc = total - mid;
        var right = NewLeaf();
        Array.Copy(leaf.Keys, mid, right.Keys, 0, rc);
        Array.Copy(leaf.Values, mid, right.Values, 0, rc);
        right.Count = rc;
        var oldRight = (Leaf?)leaf.Right;
        right.HighKey = leaf.HighKey;
        right.HasHighKey = leaf.HasHighKey;
        right.Right = oldRight;
        right.Prev = leaf;                  // splice `right` into the doubly-linked chain between leaf and oldRight
        if (oldRight != null) { oldRight.WriteLock(); oldRight.Prev = right; oldRight.WriteUnlock(); }   // sibling lock, left->right
        sepKey = right.Keys[0];
        leaf.HighKey = sepKey;
        leaf.HasHighKey = true;
        leaf.Right = right;
        leaf.Count = mid;
        Array.Clear(leaf.Keys, mid, rc);
        Array.Clear(leaf.Values, mid, rc);
        return right;
    }

    // Half-split a locked, overfull internal: the median key moves UP (becomes the separator), the keys
    // above it and their children move to the new right sibling.
    private Internal HalfSplitInternal(Internal node, out TKey sepKey)
    {
        int total = node.Count;            // _max + 1
        int mid = total / 2;               // node.Keys[mid] moves up
        int rKeys = total - mid - 1;
        var right = NewInternal();
        right.Level = node.Level;
        Array.Copy(node.Keys, mid + 1, right.Keys, 0, rKeys);
        Array.Copy(node.Children, mid + 1, right.Children, 0, rKeys + 1);
        right.Count = rKeys;
        right.HighKey = node.HighKey;
        right.HasHighKey = node.HasHighKey;
        right.Right = node.Right;
        sepKey = node.Keys[mid];
        node.HighKey = sepKey;
        node.HasHighKey = true;
        node.Right = right;
        node.Count = mid;
        Array.Clear(node.Keys, mid, rKeys + 1);
        Array.Clear(node.Children, mid + 1, rKeys + 1);
        return right;
    }

    // Install (sepKey, right) into the parent level (childLevel + 1), re-descending to find the parent
    // and moving right under its lock. Splitting upward as needed; growing a new root at the top.
    private void InsertSeparator(TKey sepKey, Node right, int childLevel)
    {
        for (; ; )
        {
            Node root = _root;
            if (root.Level == childLevel)        // the node that split was at the top level -> grow root
            {
                if (TryGrowRoot(sepKey, right, childLevel)) return;
                continue;                        // root grew under us; re-descend to find the real parent
            }

            var parent = (Internal)DescendTo(sepKey, childLevel + 1);
            parent.WriteLock();
            while (MoveRight(parent, sepKey))
            {
                var pr = (Internal)parent.Right!;
                parent.WriteUnlock(); pr.WriteLock(); parent = pr;
            }
            if (parent.Dead) { parent.WriteUnlock(); continue; }   // parent merged away -> re-descend to its absorber

            int ci = ChildIndex(parent.Keys, parent.Count, sepKey);
            InsertIntoInternal(parent, ci, sepKey, right);
            if (parent.Count <= _max)
            {
                parent.WriteUnlock();
                return;
            }
            var newRight = HalfSplitInternal(parent, out TKey psep);
            int plevel = parent.Level;
            parent.WriteUnlock();
            sepKey = psep; right = newRight; childLevel = plevel;   // continue: install psep one level up
        }
    }

    private bool TryGrowRoot(TKey sepKey, Node right, int childLevel)
    {
        lock (_growLock)
        {
            Node cur = _root;
            if (cur.Level != childLevel) return false;          // someone already grew the root
            var newRoot = NewInternal();
            newRoot.Level = childLevel + 1;
            newRoot.Keys[0] = sepKey;
            newRoot.Children[0] = cur;
            newRoot.Children[1] = right;
            newRoot.Count = 1;
            newRoot.HasHighKey = false;
            _root = newRoot;
            return true;
        }
    }

    // =====================================================================
    //  Delete — LAZY (no merge yet): descend to the leaf, lock with move-right, remove the key.
    // =====================================================================
    public bool TryRemove(TKey key, out TValue value)
    {
        if (key == null) throw new ArgumentNullException(nameof(key));
        var leaf = (Leaf)DescendTo(key, 0);
        leaf.WriteLock();
        while (MoveRight(leaf, key))
        {
            var r = (Leaf)leaf.Right!;
            leaf.WriteUnlock(); r.WriteLock(); leaf = r;
        }
        int i = Find(leaf.Keys, leaf.Count, key);
        if (i < 0) { leaf.WriteUnlock(); value = default!; return false; }
        value = leaf.Values[i];
        Array.Copy(leaf.Keys, i + 1, leaf.Keys, i, leaf.Count - i - 1);
        Array.Copy(leaf.Values, i + 1, leaf.Values, i, leaf.Count - i - 1);
        leaf.Count--;
        Array.Clear(leaf.Keys, leaf.Count, 1);
        Array.Clear(leaf.Values, leaf.Count, 1);
        bool underfull = leaf.Count < _min;
        leaf.WriteUnlock();
        _count.Decrement();
        _deletedSinceCompact.Increment();
        if (underfull && _onlineMerge) MergeOnline(key);    // online half-dead merge -> automatic reclamation
        return true;
    }

    /// <summary>Removes the pair only if both key and current value match. (ConcurrentMap.remove(k,v).)</summary>
    public bool TryRemove(KeyValuePair<TKey, TValue> item) => TryRemoveIf(item.Key, item.Value, out _);

    private bool TryRemoveIf(TKey key, TValue expected, out TValue removed)
    {
        if (key == null) throw new ArgumentNullException(nameof(key));
        var leaf = LockLeafForWrite(key);
        int i = Find(leaf.Keys, leaf.Count, key);
        if (i < 0 || !EqualityComparer<TValue>.Default.Equals(leaf.Values[i], expected)) { leaf.WriteUnlock(); removed = default!; return false; }
        removed = leaf.Values[i];
        Array.Copy(leaf.Keys, i + 1, leaf.Keys, i, leaf.Count - i - 1);
        Array.Copy(leaf.Values, i + 1, leaf.Values, i, leaf.Count - i - 1);
        leaf.Count--;
        Array.Clear(leaf.Keys, leaf.Count, 1);
        Array.Clear(leaf.Values, leaf.Count, 1);
        bool underfull = leaf.Count < _min;
        leaf.WriteUnlock();
        _count.Decrement();
        _deletedSinceCompact.Increment();
        if (underfull && _onlineMerge) MergeOnline(key);    // online half-dead merge -> automatic reclamation
        return true;
    }

    // =====================================================================
    //  Conditional replace (TryUpdate / TryReplace) — lock the leaf (move-right), CAS the value.
    // =====================================================================
    private bool DoReplace(TKey key, TValue newValue, bool hasComparison, TValue comparison)
    {
        if (key == null) throw new ArgumentNullException(nameof(key));
        var leaf = LockLeafForWrite(key);
        int i = Find(leaf.Keys, leaf.Count, key);
        if (i < 0) { leaf.WriteUnlock(); return false; }
        if (hasComparison && !EqualityComparer<TValue>.Default.Equals(leaf.Values[i], comparison)) { leaf.WriteUnlock(); return false; }
        leaf.Values[i] = newValue;
        leaf.WriteUnlock();
        return true;
    }

    public bool TryUpdate(TKey key, TValue newValue, TValue comparisonValue) => DoReplace(key, newValue, true, comparisonValue);

    public bool TryReplace(TKey key, TValue newValue, out TValue previous)
    {
        for (; ; )
        {
            if (!TryGetValue(key, out var cur)) { previous = default!; return false; }
            if (DoReplace(key, newValue, true, cur)) { previous = cur; return true; }
        }
    }

    // =====================================================================
    //  Navigable queries — first/last + floor/ceiling/lower/higher, via move-right + the leaf chain.
    // =====================================================================

    // Lock-free move-right descent returning the leaf whose range (at version `version`) contains `key`
    // (key < leaf.HighKey). The caller reads what it needs and re-validates `version`, retrying on change.
    private void LeafForKeyRead(TKey key, out Leaf leaf, out long version)
    {
        Node node = _root; var sw = new SpinWait();
        for (; ; )
        {
            if (!node.TryReadVersion(out long v)) { sw.SpinOnce(); continue; }
            if (node is Internal ind)
            {
                Node next = MoveRight(ind, key) ? ind.Right! : ind.Children[ChildIndex(ind.Keys, ind.Count, key)];
                if (!ind.Validate(v)) continue;
                node = next; sw = new SpinWait(); continue;
            }
            var lf = (Leaf)node;
            if (MoveRight(lf, key)) { Node r = lf.Right!; if (!lf.Validate(v)) continue; node = r; sw = new SpinWait(); continue; }
            leaf = lf; version = v; return;
        }
    }

    private Leaf RightmostLeaf()
    {
        Node node = _root; var sw = new SpinWait();
        for (; ; )
        {
            if (!node.TryReadVersion(out long v)) { sw.SpinOnce(); continue; }
            if (node is Internal ind) { Node child = ind.Children[ind.Count]; if (!ind.Validate(v)) continue; node = child; sw = new SpinWait(); continue; }
            var lf = (Leaf)node;
            if (lf.HasHighKey) { Node r = lf.Right!; if (!lf.Validate(v)) continue; node = r; sw = new SpinWait(); continue; }   // parent stale -> follow Right
            if (!lf.Validate(v)) continue;
            return lf;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int LowerBound(TKey[] keys, int count, TKey key)
    {
        int lo = 0, hi = count;
        while (lo < hi) { int mid = (int)(((uint)lo + (uint)hi) >> 1); if (_cmp.Compare(keys[mid], key) < 0) lo = mid + 1; else hi = mid; }
        return lo;
    }

    private bool TryFirstOfChain(Leaf? cur, out KeyValuePair<TKey, TValue> e)
    {
        while (cur != null)
        {
            if (!cur.TryReadVersion(out long v)) continue;
            int cnt = Volatile.Read(ref cur.Count);
            if (cnt > 0) { var kv = new KeyValuePair<TKey, TValue>(cur.Keys[0], cur.Values[0]); if (!cur.Validate(v)) continue; e = kv; return true; }
            Leaf? nx = (Leaf?)cur.Right; if (!cur.Validate(v)) continue; cur = nx;
        }
        e = default; return false;
    }

    private bool TryLastOfChain(Leaf? cur, out KeyValuePair<TKey, TValue> e)
    {
        while (cur != null)
        {
            if (!cur.TryReadVersion(out long v)) continue;
            int cnt = Volatile.Read(ref cur.Count);
            if (cnt > 0) { var kv = new KeyValuePair<TKey, TValue>(cur.Keys[cnt - 1], cur.Values[cnt - 1]); if (!cur.Validate(v)) continue; e = kv; return true; }
            Leaf? pv = cur.Prev; if (!cur.Validate(v)) continue; cur = pv;
        }
        e = default; return false;
    }

    public bool TryGetFirst(out KeyValuePair<TKey, TValue> entry) => TryFirstOfChain(LeftmostLeaf(), out entry);
    public bool TryGetLast(out KeyValuePair<TKey, TValue> entry) => TryLastOfChain(RightmostLeaf(), out entry);

    /// <summary>Least entry with key ≥ key (ceilingEntry).</summary>
    public bool TryGetCeiling(TKey key, out KeyValuePair<TKey, TValue> entry) => Relational(key, lower: false, inclusive: true, out entry);
    /// <summary>Least entry with key &gt; key (higherEntry).</summary>
    public bool TryGetHigher(TKey key, out KeyValuePair<TKey, TValue> entry) => Relational(key, lower: false, inclusive: false, out entry);
    /// <summary>Greatest entry with key ≤ key (floorEntry).</summary>
    public bool TryGetFloor(TKey key, out KeyValuePair<TKey, TValue> entry) => Relational(key, lower: true, inclusive: true, out entry);
    /// <summary>Greatest entry with key &lt; key (lowerEntry).</summary>
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
            LeafForKeyRead(key, out var leaf, out long v);
            int cnt = Volatile.Read(ref leaf.Count);
            if (!lower)   // ceiling / higher : first key >= key (or > key)
            {
                int i = inclusive ? LowerBound(leaf.Keys, cnt, key) : ChildIndex(leaf.Keys, cnt, key);
                if (i < cnt)
                {
                    var kv = new KeyValuePair<TKey, TValue>(leaf.Keys[i], leaf.Values[i]);
                    if (!leaf.Validate(v)) continue;
                    entry = kv; return true;
                }
                Leaf? nx = (Leaf?)leaf.Right;
                if (!leaf.Validate(v)) continue;
                return TryFirstOfChain(nx, out entry);
            }
            else          // floor / lower : last key <= key (or < key)
            {
                int i = (inclusive ? ChildIndex(leaf.Keys, cnt, key) : LowerBound(leaf.Keys, cnt, key)) - 1;
                if (i >= 0)
                {
                    var kv = new KeyValuePair<TKey, TValue>(leaf.Keys[i], leaf.Values[i]);
                    if (!leaf.Validate(v)) continue;
                    entry = kv; return true;
                }
                Leaf? pv = leaf.Prev;
                if (!leaf.Validate(v)) continue;
                return TryLastOfChain(pv, out entry);
            }
        }
    }

    /// <summary>Atomically removes and returns the smallest entry (pollFirstEntry).</summary>
    public bool TryRemoveFirst(out KeyValuePair<TKey, TValue> entry)
    {
        for (; ; ) { if (!TryGetFirst(out var f)) { entry = default; return false; } if (TryRemove(f.Key, out var v)) { entry = new(f.Key, v); return true; } }
    }

    /// <summary>Atomically removes and returns the largest entry (pollLastEntry).</summary>
    public bool TryRemoveLast(out KeyValuePair<TKey, TValue> entry)
    {
        for (; ; ) { if (!TryGetLast(out var l)) { entry = default; return false; } if (TryRemove(l.Key, out var v)) { entry = new(l.Key, v); return true; } }
    }

    // =====================================================================
    //  Bounded range scans — forward via Right, backward via Prev, snapshot + dedup.
    // =====================================================================
    private Leaf LeafForKey(TKey key) { LeafForKeyRead(key, out var leaf, out _); return leaf; }

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
            leaf = next;
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
            leaf = prev;
        }
    }

    // =====================================================================
    //  NavigableMap views
    // =====================================================================
    public RangeView GetViewBetween(TKey fromKey, TKey toKey) => GetViewBetween(fromKey, true, toKey, false);
    public RangeView GetViewBetween(TKey fromKey, bool fromInclusive, TKey toKey, bool toInclusive)
        => new(this, true, fromKey, fromInclusive, true, toKey, toInclusive, descending: false);
    public RangeView GetViewTo(TKey toKey, bool inclusive = false) => new(this, false, default!, false, true, toKey, inclusive, descending: false);
    public RangeView GetViewFrom(TKey fromKey, bool inclusive = true) => new(this, true, fromKey, inclusive, false, default!, false, descending: false);
    public RangeView Reverse() => new(this, false, default!, false, false, default!, false, descending: true);

    /// <summary>A live, navigable view over a key range (and/or reversed). Weakly consistent, like the parent.</summary>
    public sealed class RangeView : IDictionary<TKey, TValue>, IReadOnlyDictionary<TKey, TValue>
    {
        private readonly BLinkTree<TKey, TValue> _p;
        private readonly bool _hasLo, _loInc, _hasHi, _hiInc, _desc;
        private readonly TKey _lo, _hi;

        internal RangeView(BLinkTree<TKey, TValue> p, bool hasLo, TKey lo, bool loInc, bool hasHi, TKey hi, bool hiInc, bool descending)
        { _p = p; _hasLo = hasLo; _lo = lo; _loInc = loInc; _hasHi = hasHi; _hi = hi; _hiInc = hiInc; _desc = descending; }

        private bool TooLow(TKey k) { if (!_hasLo) return false; int c = _p._cmp.Compare(k, _lo); return c < 0 || (c == 0 && !_loInc); }
        private bool TooHigh(TKey k) { if (!_hasHi) return false; int c = _p._cmp.Compare(k, _hi); return c > 0 || (c == 0 && !_hiInc); }
        private bool InRange(TKey k) => !TooLow(k) && !TooHigh(k);
        private void CheckRange(TKey k) { if (!InRange(k)) throw new ArgumentOutOfRangeException(nameof(k), $"Key {k} is outside the sub-dictionary range."); }

        public IEnumerator<KeyValuePair<TKey, TValue>> GetEnumerator()
            => (_desc ? _p.ScanDescending(_hasLo, _lo, _loInc, _hasHi, _hi, _hiInc)
                      : _p.ScanAscending(_hasLo, _lo, _loInc, _hasHi, _hi, _hiInc)).GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        private bool AscFirst(out KeyValuePair<TKey, TValue> e)
        { bool ok = _hasLo ? (_loInc ? _p.TryGetCeiling(_lo, out e) : _p.TryGetHigher(_lo, out e)) : _p.TryGetFirst(out e); if (ok && TooHigh(e.Key)) { e = default; return false; } return ok; }
        private bool AscLast(out KeyValuePair<TKey, TValue> e)
        { bool ok = _hasHi ? (_hiInc ? _p.TryGetFloor(_hi, out e) : _p.TryGetLower(_hi, out e)) : _p.TryGetLast(out e); if (ok && TooLow(e.Key)) { e = default; return false; } return ok; }
        private bool RangeCeiling(TKey key, out KeyValuePair<TKey, TValue> e)
        { if (!_p.TryGetCeiling(key, out var c)) { e = default; return false; } if (TooLow(c.Key)) return AscFirst(out e); if (TooHigh(c.Key)) { e = default; return false; } e = c; return true; }
        private bool RangeHigher(TKey key, out KeyValuePair<TKey, TValue> e)
        { if (!_p.TryGetHigher(key, out var c)) { e = default; return false; } if (TooLow(c.Key)) return AscFirst(out e); if (TooHigh(c.Key)) { e = default; return false; } e = c; return true; }
        private bool RangeFloor(TKey key, out KeyValuePair<TKey, TValue> e)
        { if (!_p.TryGetFloor(key, out var c)) { e = default; return false; } if (TooHigh(c.Key)) return AscLast(out e); if (TooLow(c.Key)) { e = default; return false; } e = c; return true; }
        private bool RangeLower(TKey key, out KeyValuePair<TKey, TValue> e)
        { if (!_p.TryGetLower(key, out var c)) { e = default; return false; } if (TooHigh(c.Key)) return AscLast(out e); if (TooLow(c.Key)) { e = default; return false; } e = c; return true; }

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
        { ArgumentNullException.ThrowIfNull(array); foreach (var kv in this) { if (arrayIndex >= array.Length) throw new ArgumentException("Destination array is not long enough."); array[arrayIndex++] = kv; } }
        public RangeView Reverse() => new(_p, _hasLo, _lo, _loInc, _hasHi, _hi, _hiInc, !_desc);
    }

    // =====================================================================
    //  Conveniences / functional helpers (ConcurrentDictionary + ConcurrentSkipListMap parity)
    // =====================================================================
    public ICollection<TKey> Keys { get { var l = new List<TKey>(); foreach (var kv in this) l.Add(kv.Key); return l; } }
    public ICollection<TValue> Values { get { var l = new List<TValue>(); foreach (var kv in this) l.Add(kv.Value); return l; } }
    IEnumerable<TKey> IReadOnlyDictionary<TKey, TValue>.Keys => Keys;
    IEnumerable<TValue> IReadOnlyDictionary<TKey, TValue>.Values => Values;
    public IEnumerable<TKey> DescendingKeys { get { foreach (var kv in Reverse()) yield return kv.Key; } }

    public bool ContainsValue(TValue value)
    { var cmp = EqualityComparer<TValue>.Default; foreach (var kv in this) if (cmp.Equals(kv.Value, value)) return true; return false; }
    public TValue GetValueOrDefault(TKey key, TValue defaultValue) => TryGetValue(key, out var v) ? v : defaultValue;
    public TValue? GetValueOrDefault(TKey key) => TryGetValue(key, out var v) ? v : default;

    public TValue GetOrAdd(TKey key, TValue value)
    { for (; ; ) { if (TryGetValue(key, out var v)) return v; if (TryAdd(key, value)) return value; } }
    public TValue GetOrAdd(TKey key, Func<TKey, TValue> valueFactory)
    { for (; ; ) { if (TryGetValue(key, out var v)) return v; var nv = valueFactory(key); if (TryAdd(key, nv)) return nv; } }
    public TValue AddOrUpdate(TKey key, TValue addValue, Func<TKey, TValue, TValue> updateValueFactory)
    { for (; ; ) { if (TryGetValue(key, out var cur)) { var nv = updateValueFactory(key, cur); if (DoReplace(key, nv, true, cur)) return nv; } else if (TryAdd(key, addValue)) return addValue; } }
    public TValue AddOrUpdate(TKey key, Func<TKey, TValue> addValueFactory, Func<TKey, TValue, TValue> updateValueFactory)
    { for (; ; ) { if (TryGetValue(key, out var cur)) { var nv = updateValueFactory(key, cur); if (DoReplace(key, nv, true, cur)) return nv; } else { var add = addValueFactory(key); if (TryAdd(key, add)) return add; } } }
    public void AddRange(IEnumerable<KeyValuePair<TKey, TValue>> items) { foreach (var kv in items) this[kv.Key] = kv.Value; }

    // ---- IDictionary / ICollection ----
    public void Add(TKey key, TValue value) { if (!TryAdd(key, value)) throw new ArgumentException($"An item with the same key already exists. Key: {key}", nameof(key)); }
    public bool Remove(TKey key) => TryRemove(key, out _);
    public bool IsReadOnly => false;
    void ICollection<KeyValuePair<TKey, TValue>>.Add(KeyValuePair<TKey, TValue> item) => Add(item.Key, item.Value);
    public bool Contains(KeyValuePair<TKey, TValue> item) => TryGetValue(item.Key, out var v) && EqualityComparer<TValue>.Default.Equals(v, item.Value);
    public bool Remove(KeyValuePair<TKey, TValue> item) => TryRemove(item);
    public void CopyTo(KeyValuePair<TKey, TValue>[] array, int arrayIndex)
    {
        ArgumentNullException.ThrowIfNull(array);
        if (arrayIndex < 0 || arrayIndex > array.Length) throw new ArgumentOutOfRangeException(nameof(arrayIndex));
        foreach (var kv in this) { if (arrayIndex >= array.Length) throw new ArgumentException("Destination array is not long enough."); array[arrayIndex++] = kv; }
    }

    /// <summary>Resets to empty by swapping in a fresh leaf root. Quiescent semantics under contention.</summary>
    public void Clear() { _root = NewLeaf(); _count.Set(0); _deletedSinceCompact.Set(0); }

    // =====================================================================
    //  Compaction (lazy-delete reclamation) — QUIESCENT bulk rebuild.
    //
    //  Lazy delete never merges, so delete-heavy churn leaves underfull/empty leaves. B-link can't
    //  safely unlink nodes online (right-links would dangle), so instead of incremental merge we
    //  reclaim in bulk: snapshot all live entries and rebuild a fresh, densely-packed tree, then swap
    //  the root. CALL WHEN QUIESCENT (no concurrent writers) — like Validate; a concurrent write during
    //  the rebuild races the root swap and could be lost. `DeletedSinceCompact` tells you when it's worth
    //  doing (e.g. compact once deletes exceed, say, half of Count).
    // =====================================================================
    public long DeletedSinceCompact => _deletedSinceCompact.Sum();

    public void Compact()
    {
        var entries = new List<KeyValuePair<TKey, TValue>>(Count);
        foreach (var kv in this) entries.Add(kv);          // ascending snapshot
        _root = BulkBuild(entries);
        _count.Set(entries.Count);
        _deletedSinceCompact.Set(0);
    }

    private TKey FirstKeyOf(Node n) { while (n is Internal ind) n = ind.Children[0]; return ((Leaf)n).Keys[0]; }

    private Node BulkBuild(List<KeyValuePair<TKey, TValue>> entries)
    {
        if (entries.Count == 0) return NewLeaf();
        int leafFill = Math.Max(1, _max - 1);              // pack near full, leave a slot for later inserts

        // leaves
        var level = new List<Node>();
        for (int i = 0; i < entries.Count; i += leafFill)
        {
            var lf = NewLeaf();
            int n = Math.Min(leafFill, entries.Count - i);
            for (int j = 0; j < n; j++) { lf.Keys[j] = entries[i + j].Key; lf.Values[j] = entries[i + j].Value; }
            lf.Count = n;
            if (level.Count > 0) { var prev = (Leaf)level[^1]; prev.Right = lf; prev.HighKey = lf.Keys[0]; prev.HasHighKey = true; lf.Prev = prev; }
            level.Add(lf);
        }

        // internal levels, bottom-up
        int lvl = 0;
        int childFanout = Math.Max(2, _max);               // children per internal
        while (level.Count > 1)
        {
            lvl++;
            var parents = new List<Node>();
            for (int i = 0; i < level.Count; )
            {
                int take = Math.Min(childFanout, level.Count - i);
                var node = NewInternal();
                node.Level = lvl;
                node.Children[0] = level[i];
                for (int c = 1; c < take; c++) { node.Children[c] = level[i + c]; node.Keys[c - 1] = FirstKeyOf(level[i + c]); }
                node.Count = take - 1;
                if (parents.Count > 0) { var prev = (Internal)parents[^1]; prev.Right = node; prev.HighKey = FirstKeyOf(node); prev.HasHighKey = true; }
                parents.Add(node);
                i += take;
            }
            level = parents;
        }
        return level[0];
    }

    // =====================================================================
    //  Enumeration — leftmost leaf, then follow Right. Weakly consistent.
    // =====================================================================
    private Leaf LeftmostLeaf()
    {
        Node node = _root;
        var sw = new SpinWait();
        for (; ; )
        {
            if (node is Leaf lf) return lf;
            if (!node.TryReadVersion(out long v)) { sw.SpinOnce(); continue; }
            var ind = (Internal)node;
            Node child = ind.Children[0];
            if (!ind.Validate(v)) continue;
            node = child; sw = new SpinWait();
        }
    }

    // Snapshot a leaf's entries AND both chain links under one validated version (so a scan never
    // follows a link that doesn't match the entries it just saw — the split/merge-safe read).
    private int SnapshotLeaf(Leaf leaf, TKey[] bufK, TValue[] bufV, out Leaf? next, out Leaf? prev)
    {
        for (; ; )
        {
            if (!leaf.TryReadVersion(out long v)) continue;
            int n = Volatile.Read(ref leaf.Count);
            if (n > bufK.Length) n = bufK.Length;
            Array.Copy(leaf.Keys, bufK, n);
            Array.Copy(leaf.Values, bufV, n);
            next = (Leaf?)leaf.Right;
            prev = leaf.Prev;
            if (leaf.Validate(v)) return n;
        }
    }

    public IEnumerator<KeyValuePair<TKey, TValue>> GetEnumerator()
    {
        Leaf? leaf = LeftmostLeaf();
        bool have = false; TKey last = default!;
        var bufK = new TKey[_max + 1]; var bufV = new TValue[_max + 1];
        while (leaf != null)
        {
            int n = SnapshotLeaf(leaf, bufK, bufV, out var next, out _);
            for (int i = 0; i < n; i++)
            {
                if (have && _cmp.Compare(bufK[i], last) <= 0) continue;   // dedup / strictly ascending
                last = bufK[i]; have = true;
                yield return new KeyValuePair<TKey, TValue>(bufK[i], bufV[i]);
            }
            leaf = next;
        }
    }
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    // =====================================================================
    //  Quiescent structural validation (call with no concurrent ops).
    // =====================================================================
    public void Validate()
    {
        // 1) recursive: every node's keys are sorted and < HighKey; all leaves at level 0; child ranges nest.
        long leafTotal = 0;
        ValidateNode(_root, true, default!, _root.HasHighKey, _root.HighKey, ref leafTotal);

        // 2) leaf right-link chain is globally strictly ascending and totals _count.
        Node n = _root;
        while (n is Internal ind) n = ind.Children[0];
        bool first = true; TKey prev = default!; long chain = 0;
        for (var leaf = (Leaf?)n; leaf != null; leaf = (Leaf?)leaf.Right)
            for (int i = 0; i < leaf.Count; i++)
            {
                if (!first && _cmp.Compare(leaf.Keys[i], prev) <= 0)
                    throw new InvalidOperationException($"leaf chain not strictly ascending at {leaf.Keys[i]} after {prev}");
                prev = leaf.Keys[i]; first = false; chain++;
            }
        long cnt = _count.Sum();
        if (leafTotal != cnt) throw new InvalidOperationException($"subtree key total {leafTotal} != _count {cnt}");
        if (chain != cnt) throw new InvalidOperationException($"leaf-chain key total {chain} != _count {cnt}");
    }

    private void ValidateNode(Node node, bool hasLo, TKey lo, bool hasHi, TKey hi, ref long leafTotal)
    {
        if (node.Dead) throw new InvalidOperationException($"DEAD node reachable in structure (level {node.Level}, count {node.Count})");
        for (int i = 0; i < node.Count; i++)
        {
            if (i > 0 && _cmp.Compare(node.Keys[i - 1], node.Keys[i]) >= 0)
                throw new InvalidOperationException("keys not ascending within node");
            if (hasLo && _cmp.Compare(node.Keys[i], lo) < 0) throw new InvalidOperationException("key below lower bound");
            if (hasHi && _cmp.Compare(node.Keys[i], hi) >= 0) throw new InvalidOperationException("key at/above HighKey");
        }
        if (node is Leaf)
        {
            if (node.Level != 0) throw new InvalidOperationException($"leaf at non-zero level {node.Level}");
            leafTotal += node.Count;
            return;
        }
        var ind = (Internal)node;
        for (int i = 0; i <= ind.Count; i++)
        {
            bool cLo = i > 0 || hasLo; TKey childLo = i > 0 ? ind.Keys[i - 1] : lo;
            bool cHi = i < ind.Count || hasHi; TKey childHi = i < ind.Count ? ind.Keys[i] : hi;
            var child = ind.Children[i];
            if (child.Level != ind.Level - 1) throw new InvalidOperationException($"UNBALANCED: child level {child.Level} under internal level {ind.Level}");
            ValidateNode(child, cLo, childLo, cHi, childHi, ref leafTotal);
        }
    }

    public (int Depth, int Internals, int Leaves) DebugStats()
    {
        int depth = 0; Node n = _root;
        while (n is Internal ind) { depth++; n = ind.Children[0]; }
        int internals = 0, leaves = 0;
        CountNodes(_root, ref internals, ref leaves);
        return (depth, internals, leaves);
    }

    private void CountNodes(Node node, ref int internals, ref int leaves)
    {
        if (node is Leaf) { leaves++; return; }
        var ind = (Internal)node;
        internals++;
        for (int i = 0; i <= ind.Count; i++) CountNodes(ind.Children[i], ref internals, ref leaves);
    }
}
