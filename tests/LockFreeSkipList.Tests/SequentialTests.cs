using Mobratil.Collections;
using Xunit;

namespace LockFreeSkipList.Tests;

/// <summary>Single-threaded correctness, including model-based comparison against SortedDictionary.</summary>
public class SequentialTests
{
    [Fact]
    public void Empty_Dictionary_Has_No_Entries()
    {
        var m = new ConcurrentSkipListDictionary<int, string>();
        Assert.True(m.IsEmpty);
        Assert.Equal(0, m.Count);
        Assert.False(m.TryGetValue(0, out _));
        Assert.False(m.TryGetFirst(out _));
        Assert.False(m.TryGetLast(out _));
        Assert.Empty(m);
    }

    [Fact]
    public void Add_Get_Remove_Roundtrip()
    {
        var m = new ConcurrentSkipListDictionary<int, string>();
        Assert.True(m.TryAdd(1, "a"));
        Assert.False(m.TryAdd(1, "b"));          // already present
        Assert.True(m.TryGetValue(1, out var v));
        Assert.Equal("a", v);
        Assert.Equal(1, m.Count);

        Assert.True(m.TryRemove(1, out var removed));
        Assert.Equal("a", removed);
        Assert.False(m.TryRemove(1, out _));
        Assert.False(m.ContainsKey(1));
        Assert.Equal(0, m.Count);
    }

    [Fact]
    public void Indexer_Set_Inserts_And_Overwrites()
    {
        var m = new ConcurrentSkipListDictionary<int, string>();
        m[5] = "x";
        m[5] = "y";                 // overwrite
        Assert.Equal("y", m[5]);
        Assert.Equal(1, m.Count);
    }

    [Fact]
    public void TryUpdate_Replaces_Only_When_Comparison_Matches()
    {
        var m = new ConcurrentSkipListDictionary<int, string>();
        m[5] = "x";
        Assert.False(m.TryUpdate(5, "z", "wrong")); // comparison mismatch
        Assert.Equal("x", m[5]);
        Assert.True(m.TryUpdate(5, "y", "x"));      // comparison matches
        Assert.Equal("y", m[5]);
        Assert.False(m.TryUpdate(404, "v", "x"));   // absent key
    }

    [Fact]
    public void AddOrUpdate_Adds_Then_Updates()
    {
        var m = new ConcurrentSkipListDictionary<int, int>();
        Assert.Equal(1, m.AddOrUpdate(7, 1, (_, old) => old + 100));   // add
        Assert.Equal(101, m.AddOrUpdate(7, 1, (_, old) => old + 100)); // update
        Assert.Equal(101, m[7]);
        Assert.Equal(1, m.Count);
    }

    [Fact]
    public void Indexer_Get_Throws_When_Absent()
    {
        var m = new ConcurrentSkipListDictionary<int, int>();
        Assert.Throws<KeyNotFoundException>(() => _ = m[42]);
    }

    [Fact]
    public void GetOrAdd_Returns_Existing_Or_Adds()
    {
        var m = new ConcurrentSkipListDictionary<int, int>();
        Assert.Equal(10, m.GetOrAdd(1, 10));
        Assert.Equal(10, m.GetOrAdd(1, 999)); // existing wins
        Assert.Equal(1, m.Count);
    }

    [Fact]
    public void TryRemove_With_Expected_Value()
    {
        var m = new ConcurrentSkipListDictionary<int, int>();
        m[1] = 100;
        Assert.False(m.TryRemove(new KeyValuePair<int, int>(1, 999)));  // wrong expected
        Assert.True(m.ContainsKey(1));
        Assert.True(m.TryRemove(new KeyValuePair<int, int>(1, 100)));   // correct expected
        Assert.False(m.ContainsKey(1));
    }

    [Fact]
    public void Enumeration_Is_Sorted_Ascending()
    {
        var m = new ConcurrentSkipListDictionary<int, int>();
        int[] keys = { 50, 3, 17, 99, 1, 42, 8, 76, 23, 64 };
        foreach (var k in keys) m[k] = k * 10;

        var seen = m.Select(kv => kv.Key).ToList();
        var sorted = keys.OrderBy(x => x).ToList();
        Assert.Equal(sorted, seen);
        foreach (var kv in m) Assert.Equal(kv.Key * 10, kv.Value);
    }

    [Fact]
    public void First_And_Last()
    {
        var m = new ConcurrentSkipListDictionary<int, int>();
        foreach (var k in new[] { 7, 2, 9, 4, 1, 8 }) m[k] = k;
        Assert.True(m.TryGetFirst(out var first));
        Assert.Equal(1, first.Key);
        Assert.True(m.TryGetLast(out var last));
        Assert.Equal(9, last.Key);
    }

    [Fact]
    public void Custom_Comparer_Descending()
    {
        var m = new ConcurrentSkipListDictionary<int, int>(Comparer<int>.Create((a, b) => b.CompareTo(a)));
        foreach (var k in new[] { 1, 2, 3, 4, 5 }) m[k] = k;
        Assert.Equal(new[] { 5, 4, 3, 2, 1 }, m.Select(kv => kv.Key).ToArray());
        Assert.True(m.TryGetFirst(out var f));
        Assert.Equal(5, f.Key); // "first" under the comparator
    }

    [Fact]
    public void String_Keys()
    {
        var m = new ConcurrentSkipListDictionary<string, int>(StringComparer.Ordinal);
        foreach (var s in new[] { "banana", "apple", "cherry", "date" }) m[s] = s.Length;
        Assert.Equal(new[] { "apple", "banana", "cherry", "date" }, m.Keys.ToArray());
        Assert.Equal(6, m["banana"]);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(123)]
    public void Model_Based_Against_SortedDictionary(int seed)
    {
        var rng = new Random(seed);
        var map = new ConcurrentSkipListDictionary<int, int>();
        var model = new SortedDictionary<int, int>();
        const int keySpace = 200;

        for (int i = 0; i < 20_000; i++)
        {
            int key = rng.Next(keySpace);
            switch (rng.Next(4))
            {
                case 0: // put
                    int val = rng.Next();
                    map[key] = val;
                    model[key] = val;
                    break;
                case 1: // tryadd
                    int v2 = rng.Next();
                    bool added = map.TryAdd(key, v2);
                    bool modelAdded = !model.ContainsKey(key);
                    Assert.Equal(modelAdded, added);
                    if (modelAdded) model[key] = v2;
                    break;
                case 2: // remove
                    bool removed = map.TryRemove(key, out _);
                    bool modelRemoved = model.Remove(key);
                    Assert.Equal(modelRemoved, removed);
                    break;
                case 3: // get
                    bool got = map.TryGetValue(key, out var gv);
                    bool modelGot = model.TryGetValue(key, out var mv);
                    Assert.Equal(modelGot, got);
                    if (modelGot) Assert.Equal(mv, gv);
                    break;
            }
        }

        // Full structural equivalence at the end.
        Assert.Equal(model.Count, map.Count);
        Assert.Equal(model.ToList(), map.ToList());
        Assert.Equal(model.Keys.ToList(), map.Keys.ToList());
    }

    [Fact]
    public void Large_Sequential_Insert_Then_Delete_All()
    {
        var m = new ConcurrentSkipListDictionary<int, int>();
        const int n = 50_000;
        for (int i = 0; i < n; i++) Assert.True(m.TryAdd(i, i));
        Assert.Equal(n, m.Count);
        // strictly ascending and complete
        int expected = 0;
        foreach (var kv in m) Assert.Equal(expected++, kv.Key);
        Assert.Equal(n, expected);

        for (int i = 0; i < n; i++) Assert.True(m.TryRemove(i, out _));
        Assert.True(m.IsEmpty);
        Assert.Equal(0, m.Count);
    }
}
