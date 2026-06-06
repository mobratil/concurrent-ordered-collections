using Mobratil.Collections;
using Xunit;

namespace LockFreeSkipList.Tests;

/// <summary>Parity surface for the concurrent B+-tree: navigable queries, range/descending views,
/// functional helpers, BCL interfaces — mirroring the skip list's API — plus doubly-linked-chain
/// integrity (the Prev pointer) and concurrent range-scan correctness.</summary>
public class ConcurrentBPlusTreeParityTests
{
    private static int Threads => Math.Max(4, Environment.ProcessorCount);

    private static ConcurrentBTreeDictionary<int, int> EvenKeys(int order = 8)
    {
        var d = new ConcurrentBTreeDictionary<int, int>(order);
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
        var d = new ConcurrentBTreeDictionary<int, int>(order: 4);
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
    public void DoublyLinked_Chain_Is_Consistent_After_Churn(int order)
    {
        var d = new ConcurrentBTreeDictionary<int, int>(order);
        var rng = new Random(7);
        for (int i = 0; i < 60_000; i++)
        {
            int k = rng.Next(20_000);
            if ((rng.Next() & 1) == 0) d[k] = k; else d.TryRemove(k, out _);
        }
        // Forward (Next) and backward (Prev) chains must be exact reverses of each other.
        var asc = d.Keys.ToList();
        var desc = d.DescendingKeys.ToList();
        Assert.Equal(asc, Enumerable.Reverse(desc).ToList());
        Assert.Equal(asc.OrderBy(x => x).ToList(), asc);   // and sorted
    }

    [Fact]
    public void Range_Views()
    {
        var d = new ConcurrentBTreeDictionary<int, int>(order: 4);
        for (int i = 0; i < 10; i++) d[i] = i;
        Assert.Equal(new[] { 0, 1, 2, 3, 4 }, d.GetViewTo(5).Keys);
        Assert.Equal(new[] { 0, 1, 2, 3, 4, 5 }, d.GetViewTo(5, inclusive: true).Keys);
        Assert.Equal(new[] { 5, 6, 7, 8, 9 }, d.GetViewFrom(5).Keys);
        Assert.Equal(new[] { 3, 4, 5, 6 }, d.GetViewBetween(3, 7).Keys);
        Assert.Equal(new[] { 3, 4, 5, 6, 7 }, d.GetViewBetween(3, true, 7, true).Keys);
        Assert.Equal(new[] { 9, 8, 7, 6, 5, 4, 3, 2, 1, 0 }, d.Reverse().Keys);
        Assert.Equal(new[] { 6, 5, 4, 3 }, d.GetViewBetween(3, 7).Reverse().Keys);

        var sub = d.GetViewBetween(3, 7);
        Assert.Equal(4, sub.Count);
        Assert.True(sub.ContainsKey(5) && !sub.ContainsKey(7));
        Assert.True(sub.TryGetCeiling(0, out var c) && c.Key == 3);   // below range -> first
        Assert.True(sub.TryGetFloor(99, out var fl) && fl.Key == 6);  // above range -> last
        sub[5] = 500; Assert.Equal(500, d[5]);                        // live mutation in range
        Assert.Throws<ArgumentOutOfRangeException>(() => sub[7] = 0); // out of range
        Assert.True(sub.Remove(4) && !d.ContainsKey(4));
    }

    [Fact]
    public void Functional_And_Conveniences()
    {
        var d = new ConcurrentBTreeDictionary<string, int>(comparer: StringComparer.Ordinal);
        d.AddRange(new[] { new KeyValuePair<string, int>("a", 1), new("b", 2) });
        Assert.Equal(new[] { "a", "b" }, d.Keys);
        Assert.Same(StringComparer.Ordinal, d.Comparer);
        Assert.True(d.ContainsValue(2) && !d.ContainsValue(99));
        Assert.Equal(1, d.GetValueOrDefault("a", -1));
        Assert.Equal(-1, d.GetValueOrDefault("z", -1));

        Assert.False(d.TryUpdate("a", 10, 999));
        Assert.True(d.TryUpdate("a", 10, 1));
        Assert.Equal(10, d["a"]);
        Assert.True(d.TryReplace("b", 20, out var prev) && prev == 2);

        Assert.Equal(5, d.GetOrAdd("c", _ => 5));
        Assert.Equal(5, d.GetOrAdd("c", _ => 99));
        Assert.True(d.ComputeIfPresent("c", (_, v) => v + 1, out var nv) && nv == 6);
        Assert.Equal(100, d.AddOrUpdate("d", 100, (_, v) => v + 1));
        Assert.Equal(101, d.AddOrUpdate("d", 100, (_, v) => v + 1));
        Assert.Equal(7, d.AddOrUpdate("e", 7, (_, a) => a + 7));
        Assert.Equal(17, d.AddOrUpdate("e", 10, (_, a) => a + 10));
    }

    [Fact]
    public void Implements_Standard_Interfaces()
    {
        var d = new ConcurrentBTreeDictionary<int, string>();
        Assert.IsAssignableFrom<IDictionary<int, string>>(d);
        Assert.IsAssignableFrom<IReadOnlyDictionary<int, string>>(d);
        IDictionary<int, string> id = d;
        id.Add(1, "a");
        Assert.Throws<ArgumentException>(() => id.Add(1, "dup"));
        Assert.True(id.Remove(1));
        Assert.False(id.Remove(1));
    }

    // ---------- concurrency on the new surface ----------

    [Fact]
    public void Concurrent_Range_Scan_Stays_Sorted_And_In_Bounds()
    {
        var d = new ConcurrentBTreeDictionary<int, int>(order: 8);
        const int n = 60_000, lo = 20_000, hi = 25_000;
        for (int k = 0; k < n; k++) d[k] = k;
        var view = d.GetViewBetween(lo, hi);
        var desc = d.Reverse();

        using var stop = new CancellationTokenSource();
        var writers = new List<Task>();
        for (int w = 0; w < Threads; w++)
        {
            int seed = w;
            writers.Add(Task.Run(() =>
            {
                var rng = new Random(seed * 13 + 5);
                while (!stop.IsCancellationRequested)
                {
                    int k = rng.Next(n);
                    if ((rng.Next() & 1) == 0) d[k] = k; else d.TryRemove(k, out _);
                }
            }));
        }

        long scans = 0;
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (DateTime.UtcNow < deadline)
        {
            int prev = int.MinValue;
            foreach (var kv in view) { Assert.InRange(kv.Key, lo, hi - 1); Assert.True(kv.Key > prev); prev = kv.Key; }
            int dprev = int.MaxValue;
            foreach (var kv in desc) { Assert.True(kv.Key < dprev); dprev = kv.Key; }
            scans++;
        }
        stop.Cancel();
        Task.WaitAll(writers.ToArray());
        Assert.True(scans > 0);
    }
}
