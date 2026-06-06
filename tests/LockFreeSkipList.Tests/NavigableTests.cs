using Mobratil.Collections;
using Xunit;

namespace LockFreeSkipList.Tests;

/// <summary>Covers the NavigableMap/SortedMap surface ported from Java's ConcurrentSkipListMap.</summary>
public class NavigableTests
{
    // A dictionary of the EVEN keys 0,2,..,98 (so odd query keys are "between" entries).
    private static ConcurrentSkipListDictionary<int, int> EvenKeys()
    {
        var d = new ConcurrentSkipListDictionary<int, int>();
        for (int k = 0; k <= 98; k += 2) d[k] = k * 10;
        return d;
    }
    private static readonly int[] Present = Enumerable.Range(0, 50).Select(i => i * 2).ToArray();

    [Theory]
    [InlineData(-5)]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(47)]
    [InlineData(48)]
    [InlineData(98)]
    [InlineData(99)]
    [InlineData(1000)]
    public void Floor_Ceiling_Lower_Higher_Match_The_Oracle(int q)
    {
        var d = EvenKeys();

        int? lower = Present.Where(k => k < q).Cast<int?>().LastOrDefault();
        int? floor = Present.Where(k => k <= q).Cast<int?>().LastOrDefault();
        int? ceiling = Present.Where(k => k >= q).Cast<int?>().FirstOrDefault();
        int? higher = Present.Where(k => k > q).Cast<int?>().FirstOrDefault();

        Assert.Equal(lower.HasValue, d.TryGetLower(q, out var le));
        if (lower.HasValue) { Assert.Equal(lower.Value, le.Key); Assert.Equal(lower.Value * 10, le.Value); }

        Assert.Equal(floor.HasValue, d.TryGetFloor(q, out var fe));
        if (floor.HasValue) Assert.Equal(floor.Value, fe.Key);

        Assert.Equal(ceiling.HasValue, d.TryGetCeiling(q, out var ce));
        if (ceiling.HasValue) Assert.Equal(ceiling.Value, ce.Key);

        Assert.Equal(higher.HasValue, d.TryGetHigher(q, out var he));
        if (higher.HasValue) Assert.Equal(higher.Value, he.Key);

        // key-only variants agree
        Assert.Equal(floor.HasValue, d.TryGetFloorKey(q, out var fk));
        if (floor.HasValue) Assert.Equal(floor.Value, fk);
        Assert.Equal(higher.HasValue, d.TryGetHigherKey(q, out var hk));
        if (higher.HasValue) Assert.Equal(higher.Value, hk);
    }

    [Fact]
    public void All_Key_Only_Navigable_Variants_And_Factory_Overloads()
    {
        var d = EvenKeys();   // even keys 0..98
        Assert.True(d.TryGetLowerKey(10, out var lk) && lk == 8);
        Assert.True(d.TryGetFloorKey(10, out var fk) && fk == 10);
        Assert.True(d.TryGetCeilingKey(9, out var ck) && ck == 10);
        Assert.True(d.TryGetHigherKey(10, out var hk) && hk == 12);
        Assert.False(d.TryGetLowerKey(0, out _));   // nothing below the minimum
        Assert.False(d.TryGetHigherKey(98, out _)); // nothing above the maximum

        var m = new ConcurrentSkipListDictionary<int, int>();
        Assert.Equal(5, m.GetOrAdd(1, _ => 5));                       // factory add
        Assert.Equal(5, m.GetOrAdd(1, _ => 999));                     // existing wins
        Assert.Equal(7, m.AddOrUpdate(2, _ => 7, (_, old) => old + 1)); // factory add branch
        Assert.Equal(8, m.AddOrUpdate(2, _ => 7, (_, old) => old + 1)); // update branch
        Assert.Equal(8, m[2]);
    }

    [Fact]
    public void Poll_First_And_Last_Drain_In_Order()
    {
        var d = new ConcurrentSkipListDictionary<int, int>();
        for (int i = 0; i < 10; i++) d[i] = i;

        Assert.True(d.TryRemoveFirst(out var first));
        Assert.Equal(0, first.Key);
        Assert.True(d.TryRemoveLast(out var last));
        Assert.Equal(9, last.Key);
        Assert.Equal(8, d.Count);

        // drain the rest from the front: 1,2,...,8
        int expect = 1;
        while (d.TryRemoveFirst(out var e)) Assert.Equal(expect++, e.Key);
        Assert.Equal(9, expect);
        Assert.True(d.IsEmpty);
        Assert.False(d.TryRemoveFirst(out _));
        Assert.False(d.TryRemoveLast(out _));
    }

    [Fact]
    public void Conveniences()
    {
        var d = new ConcurrentSkipListDictionary<int, string>();
        d[1] = "a"; d[2] = "b";

        Assert.Same(Comparer<int>.Default, d.Comparer);
        Assert.True(d.ContainsValue("b"));
        Assert.False(d.ContainsValue("z"));
        Assert.Equal("a", d.GetValueOrDefault(1, "?"));
        Assert.Equal("?", d.GetValueOrDefault(99, "?"));
        Assert.Null(d.GetValueOrDefault(99));

        Assert.True(d.TryReplace(1, "A", out var prev));
        Assert.Equal("a", prev);
        Assert.Equal("A", d[1]);
        Assert.False(d.TryReplace(404, "x", out _)); // absent: no-op
        Assert.False(d.ContainsKey(404));
    }

    [Fact]
    public void Functional_Updates()
    {
        var d = new ConcurrentSkipListDictionary<string, int>();
        d.AddRange(new[] { new KeyValuePair<string, int>("a", 1), new("b", 2) });
        Assert.Equal(new[] { "a", "b" }, d.Keys);

        Assert.Equal(10, d.GetOrAdd("c", _ => 10)); // adds
        Assert.Equal(1, d.GetOrAdd("a", _ => 999)); // present wins

        Assert.Equal(2, d.AddOrUpdate("b", 5, (_, old) => old));   // present -> keep old via remap
        Assert.Equal(7, d.AddOrUpdate("new", 7, (_, old) => old + 7)); // absent -> store value
        Assert.Equal(7, d["new"]);
    }

    [Fact]
    public void SubMap_HeadMap_TailMap_Ranges()
    {
        var d = new ConcurrentSkipListDictionary<int, int>();
        for (int i = 0; i < 10; i++) d[i] = i;

        Assert.Equal(new[] { 0, 1, 2, 3, 4 }, d.GetViewTo(5).Keys);              // < 5
        Assert.Equal(new[] { 0, 1, 2, 3, 4, 5 }, d.GetViewTo(5, inclusive: true).Keys);
        Assert.Equal(new[] { 5, 6, 7, 8, 9 }, d.GetViewFrom(5).Keys);             // >= 5
        Assert.Equal(new[] { 6, 7, 8, 9 }, d.GetViewFrom(5, inclusive: false).Keys);
        Assert.Equal(new[] { 3, 4, 5, 6 }, d.GetViewBetween(3, 7).Keys);              // [3,7)
        Assert.Equal(new[] { 3, 4, 5, 6, 7 }, d.GetViewBetween(3, true, 7, true).Keys);

        var sub = d.GetViewBetween(3, 7);
        Assert.Equal(4, sub.Count);
        Assert.True(sub.ContainsKey(5));
        Assert.False(sub.ContainsKey(7));     // out of range
        Assert.False(sub.TryGetValue(9, out _));
        Assert.True(sub.TryGetFirst(out var f) && f.Key == 3);
        Assert.True(sub.TryGetLast(out var l) && l.Key == 6);

        // navigable within the sub-range is clamped
        Assert.True(sub.TryGetCeiling(0, out var c) && c.Key == 3);  // below range -> range first
        Assert.False(sub.TryGetCeiling(99, out _));                  // above range
        Assert.True(sub.TryGetFloor(99, out var fl) && fl.Key == 6); // above range -> range last
    }

    [Fact]
    public void SubMap_Is_A_Live_Mutable_View()
    {
        var d = new ConcurrentSkipListDictionary<int, int>();
        for (int i = 0; i < 10; i++) d[i] = i;
        var sub = d.GetViewBetween(3, 7);   // [3,7)

        sub[5] = 500;                       // in range
        Assert.Equal(500, d[5]);            // reflected in parent
        Assert.Throws<ArgumentOutOfRangeException>(() => sub[7] = 0);  // out of range
        Assert.Throws<ArgumentOutOfRangeException>(() => sub.Add(100, 0));

        Assert.True(sub.Remove(4));
        Assert.False(d.ContainsKey(4));     // removed from parent
        Assert.False(sub.Remove(9));        // out of range -> no-op

        d[6] = 600;                         // parent change visible in view
        Assert.Equal(600, sub[6]);

        sub.Clear();                        // clears only the range
        Assert.Equal(0, sub.Count);
        Assert.True(d.ContainsKey(2) && d.ContainsKey(8)); // outside range survive
    }

    [Fact]
    public void DescendingMap_Reverses_Order_And_Navigation()
    {
        var d = new ConcurrentSkipListDictionary<int, int>();
        for (int i = 0; i < 5; i++) d[i] = i;

        var desc = d.Reverse();
        Assert.Equal(new[] { 4, 3, 2, 1, 0 }, desc.Keys);
        Assert.Equal(new[] { 4, 3, 2, 1, 0 }, d.DescendingKeys);
        Assert.True(desc.TryGetFirst(out var f) && f.Key == 4); // first in view order = largest
        Assert.True(desc.TryGetLast(out var l) && l.Key == 0);

        // in descending view order, "higher than 2" means the next one going down -> 1
        Assert.True(desc.TryGetHigher(2, out var h) && h.Key == 1);
        Assert.True(desc.TryGetLower(2, out var lo) && lo.Key == 3);

        // descendingMap of a tailMap, and double-reverse
        Assert.Equal(new[] { 4, 3, 2 }, d.GetViewFrom(2).Reverse().Keys);
        Assert.Equal(new[] { 0, 1, 2, 3, 4 }, desc.Reverse().Keys);
    }

    [Fact]
    public void Descending_Custom_Comparer_Still_Navigates()
    {
        // parent already descending via comparer; views must respect it
        var d = new ConcurrentSkipListDictionary<int, int>(Comparer<int>.Create((a, b) => b.CompareTo(a)));
        for (int i = 0; i < 5; i++) d[i] = i;
        Assert.Equal(new[] { 4, 3, 2, 1, 0 }, d.Keys);              // descending by comparer
        Assert.True(d.TryGetFirst(out var f) && f.Key == 4);       // "first" under comparator
        Assert.True(d.TryGetCeiling(2, out var c) && c.Key == 2);  // least >= 2 under comparator
    }
}
