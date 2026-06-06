using Mobratil.Collections;
using Xunit;

namespace LockFreeSkipList.Tests;

/// <summary>
/// Verifies the type behaves correctly through the standard BCL interfaces it adopts
/// (<see cref="IDictionary{TKey,TValue}"/>, <see cref="IReadOnlyDictionary{TKey,TValue}"/>,
/// <see cref="ICollection{T}"/>, <see cref="IEnumerable{T}"/>) — not just its own methods.
/// </summary>
public class InterfaceTests
{
    [Fact]
    public void Implements_The_Standard_Dictionary_Interfaces()
    {
        var d = new ConcurrentSkipListDictionary<int, string>();
        Assert.IsAssignableFrom<IDictionary<int, string>>(d);
        Assert.IsAssignableFrom<IReadOnlyDictionary<int, string>>(d);
        Assert.IsAssignableFrom<ICollection<KeyValuePair<int, string>>>(d);
        Assert.IsAssignableFrom<IEnumerable<KeyValuePair<int, string>>>(d);
    }

    [Fact]
    public void Works_Through_IDictionary_Reference()
    {
        IDictionary<int, string> d = new ConcurrentSkipListDictionary<int, string>();
        d.Add(1, "a");
        d[2] = "b";
        Assert.True(d.ContainsKey(1));
        Assert.Equal("a", d[1]);
        Assert.Equal(2, d.Count);
        Assert.False(d.IsReadOnly);

        // Add throws on duplicate key (IDictionary contract).
        Assert.Throws<ArgumentException>(() => d.Add(1, "dup"));

        // Keys/Values are snapshots in ascending key order.
        Assert.Equal(new[] { 1, 2 }, d.Keys);
        Assert.Equal(new[] { "a", "b" }, d.Values);

        Assert.True(d.Remove(1));
        Assert.False(d.Remove(1));
        Assert.False(d.ContainsKey(1));
    }

    [Fact]
    public void Works_Through_ICollection_Of_KeyValuePair()
    {
        ICollection<KeyValuePair<int, string>> c = new ConcurrentSkipListDictionary<int, string>();
        c.Add(new KeyValuePair<int, string>(1, "a"));
        c.Add(new KeyValuePair<int, string>(2, "b"));

        Assert.True(c.Contains(new KeyValuePair<int, string>(1, "a")));
        Assert.False(c.Contains(new KeyValuePair<int, string>(1, "wrong")));

        // Remove only when both key and value match.
        Assert.False(c.Remove(new KeyValuePair<int, string>(2, "wrong")));
        Assert.True(c.Remove(new KeyValuePair<int, string>(2, "b")));
        Assert.Equal(1, c.Count);

        c.Clear();
        Assert.Equal(0, c.Count);
    }

    [Fact]
    public void CopyTo_Produces_Sorted_Snapshot()
    {
        var d = new ConcurrentSkipListDictionary<int, int>();
        foreach (var k in new[] { 5, 1, 3, 2, 4 }) d[k] = k * 10;

        var array = new KeyValuePair<int, int>[7];
        ((ICollection<KeyValuePair<int, int>>)d).CopyTo(array, 1);

        Assert.Equal(default, array[0]); // offset respected
        Assert.Equal(new[] { 1, 2, 3, 4, 5 }, array[1..6].Select(kv => kv.Key));
        Assert.Equal(new[] { 10, 20, 30, 40, 50 }, array[1..6].Select(kv => kv.Value));
    }

    [Fact]
    public void Works_Through_IReadOnlyDictionary_Reference()
    {
        var concrete = new ConcurrentSkipListDictionary<int, int>();
        concrete[1] = 10;
        concrete[2] = 20;

        IReadOnlyDictionary<int, int> ro = concrete;
        Assert.Equal(2, ro.Count);
        Assert.True(ro.ContainsKey(1));
        Assert.True(ro.TryGetValue(2, out var v) && v == 20);
        Assert.Equal(new[] { 1, 2 }, ro.Keys);
        Assert.Equal(new[] { 10, 20 }, ro.Values);
        Assert.Equal(20, ro[2]);
    }

    [Fact]
    public void Linq_Works_Via_IEnumerable()
    {
        var d = new ConcurrentSkipListDictionary<int, int>();
        for (int i = 0; i < 10; i++) d[i] = i;
        // exercises IEnumerable<KeyValuePair<,>>
        Assert.Equal(45, d.Sum(kv => kv.Value));
        Assert.Equal(Enumerable.Range(0, 10), d.Select(kv => kv.Key));
        Assert.Contains(d, kv => kv.Value == 7);
    }
}
