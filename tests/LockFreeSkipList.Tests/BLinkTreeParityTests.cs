using Ordered;
using Xunit;
using Xunit.Abstractions;

namespace LockFreeSkipList.Tests;

/// <summary>Navigable / range / functional / interface parity for the B-link tree, plus the lazy-delete
/// compaction rebuild that reclaims tombstone bloat.</summary>
public class BLinkTreeParityTests
{
    private readonly ITestOutputHelper _out;
    public BLinkTreeParityTests(ITestOutputHelper output) => _out = output;
    private static int Threads => Math.Max(4, Environment.ProcessorCount);

    private static BLinkTree<int, int> EvenKeys(int order = 8)
    {
        var d = new BLinkTree<int, int>(order);
        for (int k = 0; k <= 98; k += 2) d[k] = k * 10;
        return d;
    }
    private static readonly int[] Present = Enumerable.Range(0, 50).Select(i => i * 2).ToArray();

    [Theory]
    [InlineData(-5)] [InlineData(0)] [InlineData(1)] [InlineData(2)]
    [InlineData(47)] [InlineData(48)] [InlineData(98)] [InlineData(99)] [InlineData(1000)]
    public void Navigable_Queries_Match_Oracle(int q)
    {
        var d = EvenKeys();
        int? lower = Present.Where(k => k < q).Cast<int?>().LastOrDefault();
        int? floor = Present.Where(k => k <= q).Cast<int?>().LastOrDefault();
        int? ceil = Present.Where(k => k >= q).Cast<int?>().FirstOrDefault();
        int? higher = Present.Where(k => k > q).Cast<int?>().FirstOrDefault();

        Assert.Equal(lower.HasValue, d.TryGetLower(q, out var le)); if (lower.HasValue) { Assert.Equal(lower, le.Key); Assert.Equal(lower * 10, le.Value); }
        Assert.Equal(floor.HasValue, d.TryGetFloor(q, out var fe)); if (floor.HasValue) Assert.Equal(floor, fe.Key);
        Assert.Equal(ceil.HasValue, d.TryGetCeiling(q, out var ce)); if (ceil.HasValue) Assert.Equal(ceil, ce.Key);
        Assert.Equal(higher.HasValue, d.TryGetHigher(q, out var he)); if (higher.HasValue) Assert.Equal(higher, he.Key);
        Assert.Equal(floor.HasValue, d.TryGetFloorKey(q, out var fk)); if (floor.HasValue) Assert.Equal(floor, fk);
        Assert.Equal(lower.HasValue, d.TryGetLowerKey(q, out var lk)); if (lower.HasValue) Assert.Equal(lower, lk);
    }

    [Fact]
    public void First_Last_And_Poll()
    {
        var d = new BLinkTree<int, int>(order: 4);
        for (int i = 0; i < 10; i++) d[i] = i;
        Assert.True(d.TryGetFirst(out var f) && f.Key == 0);
        Assert.True(d.TryGetLast(out var l) && l.Key == 9);
        Assert.True(d.TryRemoveFirst(out var rf) && rf.Key == 0);
        Assert.True(d.TryRemoveLast(out var rl) && rl.Key == 9);
        Assert.Equal(8, d.Count);
        int expect = 1;
        while (d.TryRemoveFirst(out var e)) Assert.Equal(expect++, e.Key);
        Assert.True(d.IsEmpty);
    }

    [Theory]
    [InlineData(4)] [InlineData(64)]
    public void DescendingKeys_Are_Reverse_Of_Ascending(int order)
    {
        var d = new BLinkTree<int, int>(order);
        var rng = new Random(7);
        for (int i = 0; i < 50_000; i++) { int k = rng.Next(20_000); if ((rng.Next() & 1) == 0) d[k] = k; else d.TryRemove(k, out _); }
        var asc = d.Keys.ToList();
        var desc = d.DescendingKeys.ToList();
        Assert.Equal(asc, Enumerable.Reverse(desc).ToList());
        Assert.Equal(asc.OrderBy(x => x).ToList(), asc);
    }

    [Fact]
    public void Range_Views()
    {
        var d = new BLinkTree<int, int>(order: 4);
        for (int i = 0; i < 10; i++) d[i] = i;
        Assert.Equal(new[] { 0, 1, 2, 3, 4 }, d.HeadMap(5).Keys);
        Assert.Equal(new[] { 0, 1, 2, 3, 4, 5 }, d.HeadMap(5, inclusive: true).Keys);
        Assert.Equal(new[] { 5, 6, 7, 8, 9 }, d.TailMap(5).Keys);
        Assert.Equal(new[] { 3, 4, 5, 6 }, d.SubMap(3, 7).Keys);
        Assert.Equal(new[] { 3, 4, 5, 6, 7 }, d.SubMap(3, true, 7, true).Keys);
        Assert.Equal(new[] { 9, 8, 7, 6, 5, 4, 3, 2, 1, 0 }, d.DescendingMap().Keys);
        Assert.Equal(new[] { 6, 5, 4, 3 }, d.SubMap(3, 7).DescendingMap().Keys);

        var sub = d.SubMap(3, 7);
        Assert.Equal(4, sub.Count);
        Assert.True(sub.ContainsKey(5) && !sub.ContainsKey(7));
        Assert.True(sub.TryGetCeiling(0, out var c) && c.Key == 3);
        Assert.True(sub.TryGetFloor(99, out var fl) && fl.Key == 6);
        sub[5] = 500; Assert.Equal(500, d[5]);
        Assert.Throws<ArgumentOutOfRangeException>(() => sub[7] = 0);
        Assert.True(sub.Remove(4) && !d.ContainsKey(4));
    }

    [Fact]
    public void Functional_And_Conveniences()
    {
        var d = new BLinkTree<string, int>(comparer: StringComparer.Ordinal);
        d.PutAll(new[] { new KeyValuePair<string, int>("a", 1), new("b", 2) });
        Assert.Equal(new[] { "a", "b" }, d.Keys);
        Assert.Same(StringComparer.Ordinal, d.Comparer);
        Assert.True(d.ContainsValue(2) && !d.ContainsValue(99));
        Assert.Equal(1, d.GetValueOrDefault("a", -1));
        Assert.Equal(-1, d.GetValueOrDefault("z", -1));

        Assert.False(d.TryUpdate("a", 10, 999));
        Assert.True(d.TryUpdate("a", 10, 1));
        Assert.Equal(10, d["a"]);
        Assert.True(d.TryReplace("b", 20, out var prev) && prev == 2);

        Assert.Equal(5, d.ComputeIfAbsent("c", _ => 5));
        Assert.Equal(5, d.ComputeIfAbsent("c", _ => 99));
        Assert.True(d.ComputeIfPresent("c", (_, v) => v + 1, out var nv) && nv == 6);
        Assert.Equal(100, d.AddOrUpdate("d", 100, (_, v) => v + 1));
        Assert.Equal(101, d.AddOrUpdate("d", 100, (_, v) => v + 1));
        Assert.Equal(7, d.Merge("e", 7, (a, b) => a + b));
        Assert.Equal(17, d.Merge("e", 10, (a, b) => a + b));
    }

    [Fact]
    public void Implements_Standard_Interfaces()
    {
        var d = new BLinkTree<int, string>();
        Assert.IsAssignableFrom<IDictionary<int, string>>(d);
        Assert.IsAssignableFrom<IReadOnlyDictionary<int, string>>(d);
        IDictionary<int, string> id = d;
        id.Add(1, "a");
        Assert.Throws<ArgumentException>(() => id.Add(1, "dup"));
        Assert.True(id.Remove(1));
        Assert.False(id.Remove(1));
    }

    // ---------- compaction ----------

    [Theory]
    [InlineData(4)] [InlineData(64)]
    public void Compaction_Reclaims_Tombstone_Bloat(int order)
    {
        var t = new BLinkTree<int, int>(order);
        const int n = 100_000;
        for (int k = 0; k < n; k++) t[k] = k;
        // scattered delete of 90%
        var rng = new Random(99);
        var survivors = new SortedSet<int>();
        for (int k = 0; k < n; k++) { if (rng.Next(10) == 0) survivors.Add(k); else t.TryRemove(k, out _); }

        var (_, _, leavesBefore) = t.DebugStats();
        Assert.True(t.DeletedSinceCompact > 0);

        t.Compact();                                            // quiescent bulk rebuild

        t.Validate();
        Assert.Equal(survivors.Count, t.Count);
        Assert.Equal(survivors.ToList(), t.Keys.ToList());      // exact survivors, in order
        Assert.Equal(0, t.DeletedSinceCompact);
        var (_, _, leavesAfter) = t.DebugStats();
        _out.WriteLine($"order={order} compact: {leavesBefore} -> {leavesAfter} leaves for {t.Count} keys");
        // densely repacked: ~ Count / (order-1) leaves, far fewer than the tombstone-bloated tree
        Assert.True(leavesAfter < leavesBefore / 3, $"compaction weak: {leavesAfter}/{leavesBefore}");
        Assert.True(leavesAfter <= survivors.Count / (order - 1) + 2);

        // still fully usable after compaction
        t[500_000] = 7; Assert.True(t.TryGetValue(500_000, out var v) && v == 7);
        Assert.True(t.TryGetCeiling(-1, out var c) && c.Key == survivors.Min);
    }

    [Fact]
    public void Compaction_On_Empty_And_Tiny()
    {
        var t = new BLinkTree<int, int>(order: 4);
        t.Compact(); Assert.True(t.IsEmpty); t.Validate();
        t[1] = 1; t.Compact(); Assert.Equal(1, t.Count); Assert.True(t.TryGetValue(1, out var v) && v == 1); t.Validate();
    }

    [Fact]
    public void Concurrent_Range_Scan_Stays_Sorted_And_In_Bounds()
    {
        var d = new BLinkTree<int, int>(order: 8);
        const int n = 60_000, lo = 20_000, hi = 25_000;
        for (int k = 0; k < n; k++) d[k] = k;
        var view = d.SubMap(lo, hi);

        using var stop = new CancellationTokenSource();
        var writers = new List<Task>();
        for (int w = 0; w < Threads; w++)
        {
            int seed = w;
            writers.Add(Task.Run(() =>
            {
                var rng = new Random(seed * 13 + 5);
                while (!stop.IsCancellationRequested) { int k = rng.Next(n); if ((rng.Next() & 1) == 0) d[k] = k; else d.TryRemove(k, out _); }
            }));
        }
        long scans = 0;
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (DateTime.UtcNow < deadline)
        {
            int prev = int.MinValue;
            foreach (var kv in view) { Assert.InRange(kv.Key, lo, hi - 1); Assert.True(kv.Key > prev); prev = kv.Key; }
            scans++;
        }
        stop.Cancel();
        Task.WaitAll(writers.ToArray());
        Assert.True(scans > 0);
    }
}
