using Mobratil.Collections;
using Xunit;

namespace LockFreeSkipList.Tests;

/// <summary>STEP 1 — single-threaded B+-tree correctness, model-checked against SortedDictionary.
/// Small orders are used deliberately to force many splits and exercise the propagation logic.</summary>
public class BPlusTreeSequentialTests
{
    [Fact]
    public void Empty_Tree()
    {
        var t = new BPlusTree<int, int>(order: 4);
        Assert.Equal(0, t.Count);
        Assert.True(t.IsEmpty);
        Assert.False(t.TryGetValue(1, out _));
        Assert.Empty(t);
        t.Validate();
    }

    [Fact]
    public void Add_Get_Remove_Roundtrip()
    {
        var t = new BPlusTree<int, string>(order: 4);
        Assert.True(t.TryAdd(1, "a"));
        Assert.False(t.TryAdd(1, "b"));        // already present
        Assert.True(t.TryGetValue(1, out var v) && v == "a");
        t[2] = "two";                          // indexer set
        Assert.Equal("two", t[2]);
        Assert.True(t.TryRemove(1, out var r) && r == "a");
        Assert.False(t.ContainsKey(1));
        Assert.Equal(1, t.Count);
        t.Validate();
    }

    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(8)]
    [InlineData(32)]
    public void Ascending_Insert_Stays_Sorted_And_Complete(int order)
    {
        var t = new BPlusTree<int, int>(order);
        const int n = 5000;
        for (int i = 0; i < n; i++) Assert.True(t.TryAdd(i, i * 10));
        t.Validate();
        Assert.Equal(n, t.Count);

        int expect = 0;
        foreach (var kv in t) { Assert.Equal(expect, kv.Key); Assert.Equal(expect * 10, kv.Value); expect++; }
        Assert.Equal(n, expect);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(16)]
    public void Descending_And_Random_Insert(int order)
    {
        var t = new BPlusTree<int, int>(order);
        const int n = 5000;
        for (int i = n - 1; i >= 0; i--) t[i] = i;          // descending inserts
        t.Validate();
        Assert.Equal(n, t.Count);
        Assert.Equal(Enumerable.Range(0, n), t.Keys);
    }

    [Theory]
    [InlineData(1, 3)]
    [InlineData(7, 4)]
    [InlineData(42, 8)]
    [InlineData(99, 64)]
    public void Model_Based_Against_SortedDictionary(int seed, int order)
    {
        var rng = new Random(seed);
        var tree = new BPlusTree<int, int>(order);
        var model = new SortedDictionary<int, int>();
        const int keySpace = 500;

        for (int i = 0; i < 40_000; i++)
        {
            int key = rng.Next(keySpace);
            switch (rng.Next(4))
            {
                case 0: // indexer set (insert or overwrite)
                    int val = rng.Next();
                    tree[key] = val;
                    model[key] = val;
                    break;
                case 1: // tryadd
                    int v2 = rng.Next();
                    bool added = tree.TryAdd(key, v2);
                    bool modelAdded = !model.ContainsKey(key);
                    Assert.Equal(modelAdded, added);
                    if (modelAdded) model[key] = v2;
                    break;
                case 2: // remove
                    bool removed = tree.TryRemove(key, out _);
                    Assert.Equal(model.Remove(key), removed);
                    break;
                case 3: // get
                    bool got = tree.TryGetValue(key, out var gv);
                    bool mGot = model.TryGetValue(key, out var mv);
                    Assert.Equal(mGot, got);
                    if (mGot) Assert.Equal(mv, gv);
                    break;
            }

            if ((i & 0x7FF) == 0) tree.Validate();   // periodic structural check
        }

        tree.Validate();
        Assert.Equal(model.Count, tree.Count);
        Assert.Equal(model.ToList(), tree.ToList());            // same entries, same order
        Assert.Equal(model.Keys.ToList(), tree.Keys.ToList());
    }

    [Fact]
    public void Large_Insert_Then_Delete_All()
    {
        var t = new BPlusTree<int, int>(order: 16);
        const int n = 50_000;
        var rng = new Random(123);
        var order = Enumerable.Range(0, n).ToArray();
        for (int i = order.Length - 1; i > 0; i--) { int j = rng.Next(i + 1); (order[i], order[j]) = (order[j], order[i]); }

        foreach (var k in order) Assert.True(t.TryAdd(k, k));
        t.Validate();
        Assert.Equal(n, t.Count);

        foreach (var k in order) Assert.True(t.TryRemove(k, out _));
        t.Validate();
        Assert.True(t.IsEmpty);
        Assert.Empty(t);
    }
}
