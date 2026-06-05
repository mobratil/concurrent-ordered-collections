using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Ordered;

/// <summary>
/// EXPERIMENT — a Lehman &amp; Yao B-link tree, to compare write-scalability against the OLC
/// <see cref="ConcurrentBPlusTree{TKey,TValue}"/>.
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
public sealed class BLinkTree<TKey, TValue>
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

    private sealed class Leaf : Node { internal TValue[] Values = default!; }
    private sealed class Internal : Node { internal Node[] Children = default!; }

    private readonly IComparer<TKey> _cmp;
    private readonly int _max;
    private volatile Node _root;
    private readonly object _growLock = new();
    private long _count;

    public BLinkTree(int order = 64, IComparer<TKey>? comparer = null)
    {
        if (order < 3) throw new ArgumentOutOfRangeException(nameof(order), "order must be >= 3");
        _max = order;
        _cmp = comparer ?? Comparer<TKey>.Default;
        _root = NewLeaf();
    }

    public int Count => (int)Math.Min(int.MaxValue, Interlocked.Read(ref _count));
    public bool IsEmpty => Interlocked.Read(ref _count) == 0;
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

    private bool Insert(TKey key, TValue value, bool onlyIfAbsent)
    {
        if (key == null) throw new ArgumentNullException(nameof(key));
        var leaf = (Leaf)DescendTo(key, 0);
        leaf.WriteLock();
        // move-right under the lock (the leaf may have split since we read it)
        while (MoveRight(leaf, key))
        {
            var r = (Leaf)leaf.Right!;
            leaf.WriteUnlock(); r.WriteLock(); leaf = r;
        }

        int idx = Find(leaf.Keys, leaf.Count, key);
        if (idx >= 0)
        {
            if (!onlyIfAbsent) leaf.Values[idx] = value;
            leaf.WriteUnlock();
            return false;
        }

        InsertIntoLeaf(leaf, ~idx, key, value);
        Interlocked.Increment(ref _count);
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
        right.HighKey = leaf.HighKey;
        right.HasHighKey = leaf.HasHighKey;
        right.Right = leaf.Right;
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
        leaf.WriteUnlock();
        Interlocked.Decrement(ref _count);
        return true;
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

    private int SnapshotLeaf(Leaf leaf, TKey[] bufK, TValue[] bufV, out Node? right)
    {
        for (; ; )
        {
            if (!leaf.TryReadVersion(out long v)) continue;
            int n = Volatile.Read(ref leaf.Count);
            if (n > bufK.Length) n = bufK.Length;
            Array.Copy(leaf.Keys, bufK, n);
            Array.Copy(leaf.Values, bufV, n);
            right = leaf.Right;
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
            int n = SnapshotLeaf(leaf, bufK, bufV, out var right);
            for (int i = 0; i < n; i++)
            {
                if (have && _cmp.Compare(bufK[i], last) <= 0) continue;   // dedup / strictly ascending
                last = bufK[i]; have = true;
                yield return new KeyValuePair<TKey, TValue>(bufK[i], bufV[i]);
            }
            leaf = (Leaf?)right;
        }
    }

    public IEnumerable<TKey> Keys { get { foreach (var kv in Iterate()) yield return kv.Key; } }
    private IEnumerable<KeyValuePair<TKey, TValue>> Iterate() { var e = GetEnumerator(); while (e.MoveNext()) yield return e.Current; }

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
        long cnt = Interlocked.Read(ref _count);
        if (leafTotal != cnt) throw new InvalidOperationException($"subtree key total {leafTotal} != _count {cnt}");
        if (chain != cnt) throw new InvalidOperationException($"leaf-chain key total {chain} != _count {cnt}");
    }

    private void ValidateNode(Node node, bool hasLo, TKey lo, bool hasHi, TKey hi, ref long leafTotal)
    {
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
