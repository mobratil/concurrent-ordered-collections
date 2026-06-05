using System;
using System.Collections;
using System.Collections.Generic;

namespace Ordered;

/// <summary>
/// STEP 1: a plain, single-threaded B+-tree (sorted map). No concurrency yet — the goal
/// here is to be boringly correct, validated against SortedDictionary, before any
/// optimistic-lock-coupling is layered on in step 2.
///
/// Layout: internal nodes hold separator keys + child pointers (children = keys + 1);
/// leaves hold sorted key/value pairs and a right-sibling link for range scans. Routing:
/// child[i] holds keys in [keys[i-1], keys[i]); a separator is a copy of the right leaf's
/// first key. Deletes are "lazy" (remove from the leaf, no merge/rebalance) — a common,
/// legitimate simplification that keeps routing correct (separators are routing keys, they
/// need not be present keys).
/// </summary>
public sealed class BPlusTree<TKey, TValue> : IEnumerable<KeyValuePair<TKey, TValue>>
{
    private abstract class Node
    {
        internal int Count;          // number of keys in use
        internal TKey[] Keys = default!;
    }

    private sealed class Leaf : Node
    {
        internal TValue[] Values = default!;
        internal Leaf? Next;         // right sibling (ascending) — for range scans
    }

    private sealed class Internal : Node
    {
        internal Node[] Children = default!;   // Count + 1 children in use
    }

    private readonly IComparer<TKey> _cmp;
    private readonly int _max;       // max keys per node before a split
    private Node _root;
    private int _count;

    public BPlusTree(int order = 32, IComparer<TKey>? comparer = null)
    {
        if (order < 3) throw new ArgumentOutOfRangeException(nameof(order), "order must be >= 3");
        _max = order;
        _cmp = comparer ?? Comparer<TKey>.Default;
        _root = NewLeaf();
    }

    public int Count => _count;
    public bool IsEmpty => _count == 0;

    private Leaf NewLeaf() => new() { Keys = new TKey[_max + 1], Values = new TValue[_max + 1], Count = 0 };
    private Internal NewInternal() => new() { Keys = new TKey[_max + 1], Children = new Node[_max + 2], Count = 0 };

    // ---------------------------------------------------------------------
    //  Search helpers
    // ---------------------------------------------------------------------

    // Index of `key` in a sorted array, or ~insertionPoint if absent (like Array.BinarySearch).
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

    // Child to descend into: first index i with keys[i] > key (i.e. count of keys <= key).
    private int ChildIndex(Internal n, TKey key)
    {
        int lo = 0, hi = n.Count;
        while (lo < hi)
        {
            int mid = (int)(((uint)lo + (uint)hi) >> 1);
            if (_cmp.Compare(key, n.Keys[mid]) >= 0) lo = mid + 1; else hi = mid;
        }
        return lo;
    }

    // ---------------------------------------------------------------------
    //  Lookup
    // ---------------------------------------------------------------------
    public bool TryGetValue(TKey key, out TValue value)
    {
        Node n = _root;
        while (n is Internal ind) n = ind.Children[ChildIndex(ind, key)];
        var leaf = (Leaf)n;
        int i = Find(leaf.Keys, leaf.Count, key);
        if (i >= 0) { value = leaf.Values[i]; return true; }
        value = default!;
        return false;
    }

    public bool ContainsKey(TKey key) => TryGetValue(key, out _);

    public TValue this[TKey key]
    {
        get => TryGetValue(key, out var v) ? v : throw new KeyNotFoundException();
        set => Insert(key, value, onlyIfAbsent: false);
    }

    public bool TryAdd(TKey key, TValue value) => Insert(key, value, onlyIfAbsent: true);

    // ---------------------------------------------------------------------
    //  Insert (recursive, with split propagation)
    // ---------------------------------------------------------------------
    private struct Split { public bool Did; public TKey Key; public Node Right; }

    // Returns true if a NEW key was added (count grew); false if it was a replace / already-present.
    private bool Insert(TKey key, TValue value, bool onlyIfAbsent)
    {
        bool added = InsertRec(_root, key, value, onlyIfAbsent, out var split);
        if (split.Did)
        {
            var newRoot = NewInternal();
            newRoot.Keys[0] = split.Key;
            newRoot.Children[0] = _root;
            newRoot.Children[1] = split.Right;
            newRoot.Count = 1;
            _root = newRoot;
        }
        if (added) _count++;
        return added;
    }

    private bool InsertRec(Node node, TKey key, TValue value, bool onlyIfAbsent, out Split split)
    {
        split = default;
        if (node is Leaf leaf)
        {
            int i = Find(leaf.Keys, leaf.Count, key);
            if (i >= 0)
            {
                if (!onlyIfAbsent) leaf.Values[i] = value;   // replace
                return false;                                // no new key
            }
            int at = ~i;
            // shift right to make room
            Array.Copy(leaf.Keys, at, leaf.Keys, at + 1, leaf.Count - at);
            Array.Copy(leaf.Values, at, leaf.Values, at + 1, leaf.Count - at);
            leaf.Keys[at] = key;
            leaf.Values[at] = value;
            leaf.Count++;
            if (leaf.Count > _max) SplitLeaf(leaf, out split);
            return true;
        }

        var ind = (Internal)node;
        int ci = ChildIndex(ind, key);
        bool added = InsertRec(ind.Children[ci], key, value, onlyIfAbsent, out var childSplit);
        if (childSplit.Did)
            InsertChildSplit(ind, ci, childSplit, out split);
        return added;
    }

    private void SplitLeaf(Leaf leaf, out Split split)
    {
        int mid = (_max + 1) / 2;          // left keeps `mid`, right gets the rest
        int rc = leaf.Count - mid;
        var right = NewLeaf();
        Array.Copy(leaf.Keys, mid, right.Keys, 0, rc);
        Array.Copy(leaf.Values, mid, right.Values, 0, rc);
        right.Count = rc;
        // clear the moved-out slots in the left leaf (avoid leaking references)
        Array.Clear(leaf.Keys, mid, rc);
        Array.Clear(leaf.Values, mid, rc);
        leaf.Count = mid;
        right.Next = leaf.Next;
        leaf.Next = right;
        split = new Split { Did = true, Key = right.Keys[0], Right = right };   // separator = right's first key
    }

    // Insert (separator, newRightChild) into an internal node at position ci, splitting if it overflows.
    private void InsertChildSplit(Internal ind, int ci, Split childSplit, out Split split)
    {
        split = default;
        // shift keys/children right
        Array.Copy(ind.Keys, ci, ind.Keys, ci + 1, ind.Count - ci);
        Array.Copy(ind.Children, ci + 1, ind.Children, ci + 2, ind.Count - ci);
        ind.Keys[ci] = childSplit.Key;
        ind.Children[ci + 1] = childSplit.Right;
        ind.Count++;
        if (ind.Count > _max) SplitInternal(ind, out split);
    }

    private void SplitInternal(Internal node, out Split split)
    {
        int mid = node.Count / 2;          // node.Keys[mid] moves UP (not copied)
        var right = NewInternal();
        int rKeys = node.Count - mid - 1;
        Array.Copy(node.Keys, mid + 1, right.Keys, 0, rKeys);
        Array.Copy(node.Children, mid + 1, right.Children, 0, rKeys + 1);
        right.Count = rKeys;
        TKey up = node.Keys[mid];
        // clear moved-out slots on the left
        Array.Clear(node.Keys, mid, node.Count - mid);
        Array.Clear(node.Children, mid + 1, node.Count - mid);
        node.Count = mid;
        split = new Split { Did = true, Key = up, Right = right };
    }

    // ---------------------------------------------------------------------
    //  Delete (lazy: remove from the leaf, no merge)
    // ---------------------------------------------------------------------
    public bool TryRemove(TKey key, out TValue value)
    {
        Node n = _root;
        while (n is Internal ind) n = ind.Children[ChildIndex(ind, key)];
        var leaf = (Leaf)n;
        int i = Find(leaf.Keys, leaf.Count, key);
        if (i < 0) { value = default!; return false; }
        value = leaf.Values[i];
        Array.Copy(leaf.Keys, i + 1, leaf.Keys, i, leaf.Count - i - 1);
        Array.Copy(leaf.Values, i + 1, leaf.Values, i, leaf.Count - i - 1);
        leaf.Count--;
        Array.Clear(leaf.Keys, leaf.Count, 1);
        Array.Clear(leaf.Values, leaf.Count, 1);
        _count--;
        return true;
    }

    // ---------------------------------------------------------------------
    //  Ordered enumeration (leftmost leaf, follow sibling links)
    // ---------------------------------------------------------------------
    public IEnumerator<KeyValuePair<TKey, TValue>> GetEnumerator()
    {
        Node n = _root;
        while (n is Internal ind) n = ind.Children[0];
        var leaf = (Leaf)n;
        while (leaf != null)
        {
            for (int i = 0; i < leaf.Count; i++)
                yield return new KeyValuePair<TKey, TValue>(leaf.Keys[i], leaf.Values[i]);
            leaf = leaf.Next;
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public IEnumerable<TKey> Keys { get { foreach (var kv in this) yield return kv.Key; } }
    public IEnumerable<TValue> Values { get { foreach (var kv in this) yield return kv.Value; } }

    // ---------------------------------------------------------------------
    //  Debug / test hook: validate structural invariants (sorted, separators correct, leaf chain).
    // ---------------------------------------------------------------------
    public void Validate()
    {
        int leafTotal = 0;
        ValidateNode(_root, false, default!, false, default!, ref leafTotal);
        if (leafTotal != _count) throw new InvalidOperationException($"Count mismatch: leaves hold {leafTotal}, _count={_count}");

        // leaf chain is globally ascending
        Node n = _root;
        while (n is Internal ind) n = ind.Children[0];
        var leaf = (Leaf)n;
        bool first = true; TKey prev = default!;
        while (leaf != null)
        {
            for (int i = 0; i < leaf.Count; i++)
            {
                if (!first && _cmp.Compare(leaf.Keys[i], prev) <= 0)
                    throw new InvalidOperationException("leaf chain not strictly ascending");
                prev = leaf.Keys[i]; first = false;
            }
            leaf = leaf.Next;
        }
    }

    private void ValidateNode(Node node, bool hasLo, TKey lo, bool hasHi, TKey hi, ref int leafTotal)
    {
        // every key strictly ascending within the node and inside the (lo, hi) window
        for (int i = 0; i < node.Count; i++)
        {
            if (i > 0 && _cmp.Compare(node.Keys[i - 1], node.Keys[i]) >= 0)
                throw new InvalidOperationException("keys not ascending within node");
            if (hasLo && _cmp.Compare(node.Keys[i], lo) < 0)
                throw new InvalidOperationException("key below separator lower bound");
            if (hasHi && _cmp.Compare(node.Keys[i], hi) >= 0)
                throw new InvalidOperationException("key at/above separator upper bound");
        }

        if (node is Leaf leaf) { leafTotal += leaf.Count; return; }

        var ind = (Internal)node;
        for (int i = 0; i <= ind.Count; i++)
        {
            bool cLo = i > 0 || hasLo;
            TKey childLo = i > 0 ? ind.Keys[i - 1] : lo;
            bool cHi = i < ind.Count || hasHi;
            TKey childHi = i < ind.Count ? ind.Keys[i] : hi;
            ValidateNode(ind.Children[i], cLo, childLo, cHi, childHi, ref leafTotal);
        }
    }
}
