using LockFree;
using Xunit;
using Xunit.Abstractions;

namespace LockFreeSkipList.Tests;

/// <summary>
/// Integrity when RangeView mutations and parent-dictionary operations run concurrently
/// (and vice-versa). A view write is the parent write after a bounds check, so these
/// pin down that (a) writes confined to a range never disturb out-of-range entries,
/// (b) a view stays sorted and within bounds while the parent is hammered, and
/// (c) view inserts are linearizable just like the parent's.
/// </summary>
public class RangeViewConcurrencyTests
{
    private readonly ITestOutputHelper _out;
    public RangeViewConcurrencyTests(ITestOutputHelper output) => _out = output;

    private static int Threads => Math.Max(4, Environment.ProcessorCount);

    /// <summary>
    /// Writers mutate ONLY through a sub-range view; readers read the whole parent. The
    /// keys outside the view's range must stay byte-for-byte intact the entire time, and
    /// a deterministic final drain (via the view) must leave exactly the out-of-range set.
    /// </summary>
    [Fact]
    public void View_Writes_Plus_Dictionary_Reads_Leave_Out_Of_Range_Untouched()
    {
        var d = new LockFreeSkipListDictionary<int, int>();
        const int n = 120_000, lo = 40_000, hi = 80_000;   // view range [lo, hi)
        for (int k = 0; k < n; k++) d[k] = k;
        var view = d.SubMap(lo, hi);

        using var stop = new CancellationTokenSource();
        var writers = new List<Task>();
        for (int w = 0; w < Threads; w++)
        {
            int seed = w;
            writers.Add(Task.Run(() =>
            {
                var rng = new Random(seed * 7 + 1);
                while (!stop.IsCancellationRequested)
                {
                    int key = lo + rng.Next(hi - lo);          // always in range
                    if ((rng.Next() & 1) == 0) view[key] = key * 2;
                    else view.Remove(key);
                }
            }));
        }

        // Reader: full-dictionary enumeration must stay sorted, and every out-of-range
        // key must remain present with its original value (writers never touch them).
        long passes = 0;
        var rngR = new Random(999);
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (DateTime.UtcNow < deadline)
        {
            int prev = int.MinValue;
            foreach (var kv in d)
            {
                Assert.True(kv.Key > prev, $"order broken: {kv.Key} after {prev}");
                prev = kv.Key;
            }
            int probe = rngR.Next(n);
            if (probe < lo || probe >= hi)
                Assert.True(d.TryGetValue(probe, out var v) && v == probe,
                    $"out-of-range key {probe} was disturbed");
            passes++;
        }
        stop.Cancel();
        Task.WaitAll(writers.ToArray());
        _out.WriteLine($"{passes} read passes during concurrent view writes");

        // Deterministic drain through the view, then the state must be exactly out-of-range.
        view.Clear();
        for (int k = lo; k < hi; k++) Assert.False(d.ContainsKey(k));
        for (int k = 0; k < lo; k++) Assert.True(d.TryGetValue(k, out var v) && v == k);
        for (int k = hi; k < n; k++) Assert.True(d.TryGetValue(k, out var v) && v == k);
        Assert.Equal(lo + (n - hi), d.Count);
    }

    /// <summary>
    /// The inverse: writers mutate the WHOLE parent (in and out of range) while readers
    /// enumerate a sub-range view. The view must always be strictly ascending and every
    /// yielded key strictly within bounds — never out of range, never unsorted, never throw.
    /// </summary>
    [Fact]
    public void Dictionary_Writes_Plus_View_Reads_Stay_Sorted_And_In_Range()
    {
        var d = new LockFreeSkipListDictionary<int, int>();
        const int n = 60_000, lo = 20_000, hi = 25_000;   // small range -> cheap view scans
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
                while (!stop.IsCancellationRequested)
                {
                    int key = rng.Next(n);                 // anywhere in the parent
                    if ((rng.Next() & 1) == 0) d[key] = key;
                    else d.TryRemove(key, out _);
                }
            }));
        }

        long enums = 0;
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (DateTime.UtcNow < deadline)
        {
            int prev = int.MinValue;
            foreach (var kv in view)
            {
                Assert.True(kv.Key >= lo && kv.Key < hi, $"view yielded out-of-range key {kv.Key}");
                Assert.True(kv.Key > prev, $"view not sorted: {kv.Key} after {prev}");
                prev = kv.Key;
            }
            // navigable queries on the view must also respect the bounds
            if (view.TryGetCeiling(lo, out var c)) Assert.InRange(c.Key, lo, hi - 1);
            if (view.TryGetFloor(hi, out var f)) Assert.InRange(f.Key, lo, hi - 1);
            enums++;
        }
        stop.Cancel();
        Task.WaitAll(writers.ToArray());
        _out.WriteLine($"{enums} view enumerations during concurrent parent writes");
        Assert.True(enums > 0);
    }

    /// <summary>
    /// A descending view enumerated while the parent is mutated must stay strictly
    /// DESCENDING and in range — exercises the reversed relational stepping under load.
    /// </summary>
    [Fact]
    public void Descending_View_Stays_Descending_Under_Concurrent_Mutation()
    {
        var d = new LockFreeSkipListDictionary<int, int>();
        const int n = 40_000, lo = 10_000, hi = 20_000;
        for (int k = 0; k < n; k++) d[k] = k;
        var desc = d.SubMap(lo, true, hi, false).DescendingMap();

        using var stop = new CancellationTokenSource();
        var writers = new List<Task>();
        for (int w = 0; w < Threads; w++)
        {
            int seed = w;
            writers.Add(Task.Run(() =>
            {
                var rng = new Random(seed * 17 + 3);
                while (!stop.IsCancellationRequested)
                {
                    int key = rng.Next(n);
                    if ((rng.Next() & 1) == 0) d[key] = key; else d.TryRemove(key, out _);
                }
            }));
        }

        long enums = 0;
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (DateTime.UtcNow < deadline)
        {
            int prev = int.MaxValue;
            foreach (var kv in desc)
            {
                Assert.True(kv.Key >= lo && kv.Key < hi, $"out-of-range {kv.Key}");
                Assert.True(kv.Key < prev, $"not descending: {kv.Key} after {prev}");
                prev = kv.Key;
            }
            enums++;
        }
        stop.Cancel();
        Task.WaitAll(writers.ToArray());
        Assert.True(enums > 0);
    }

    /// <summary>
    /// All threads race to insert the same in-range keys *through the view*. Insert must
    /// succeed exactly once per key — linearizable, just like inserting on the parent.
    /// </summary>
    [Fact]
    public void Concurrent_View_TryAdd_Succeeds_Exactly_Once()
    {
        var d = new LockFreeSkipListDictionary<int, int>();
        const int lo = 1_000, hi = 21_000;               // 20k in-range keys
        var view = d.SubMap(lo, hi);
        long succeeded = 0;

        Parallel.For(0, Threads, t =>
        {
            long local = 0;
            var rng = new Random(t * 31 + 1);
            var order = Enumerable.Range(lo, hi - lo).ToArray();
            for (int i = order.Length - 1; i > 0; i--) { int j = rng.Next(i + 1); (order[i], order[j]) = (order[j], order[i]); }
            foreach (var k in order) if (view.TryAdd(k, k)) local++;
            Interlocked.Add(ref succeeded, local);
        });

        Assert.Equal(hi - lo, succeeded);                 // exactly one winner per key
        Assert.Equal(hi - lo, view.Count);
        Assert.Equal(hi - lo, d.Count);                   // nothing leaked outside the range
        for (int k = lo; k < hi; k++) Assert.True(view.ContainsKey(k));
    }

    /// <summary>
    /// View writes and parent writes interleaved on overlapping keys, then a deterministic
    /// reconcile: out-of-range owned by the parent, in-range owned by the view, asserting
    /// no operation through either handle corrupted the shared structure.
    /// </summary>
    [Fact]
    public void Interleaved_View_And_Parent_Writes_Keep_Structure_Consistent()
    {
        var d = new LockFreeSkipListDictionary<long, long>();
        const int n = 100_000, lo = 30_000, hi = 70_000;
        var view = d.SubMap(lo, hi);

        Parallel.For(0, Threads, t =>
        {
            var rng = new Random(t * 19 + 7);
            for (int i = 0; i < 150_000; i++)
            {
                int key = rng.Next(n);
                bool inRange = key >= lo && key < hi;
                switch (rng.Next(4))
                {
                    case 0: d[key] = key; break;                          // parent write (anywhere)
                    case 1: d.TryRemove(key, out _); break;              // parent remove
                    case 2: if (inRange) view[key] = key; break;        // view write (in-range)
                    default: if (inRange) view.Remove(key); break;      // view remove
                }
            }
        });

        // Invariant check: strictly sorted, unique, values self-consistent, count agrees,
        // and the view reports exactly the in-range slice of the parent.
        long prev = long.MinValue, count = 0, inRangeCount = 0;
        foreach (var kv in d)
        {
            Assert.True(kv.Key > prev, $"order/uniqueness broken at {kv.Key}");
            Assert.Equal(kv.Key, kv.Value);
            prev = kv.Key;
            count++;
            if (kv.Key >= lo && kv.Key < hi) inRangeCount++;
        }
        Assert.Equal(count, d.Count);
        Assert.Equal(inRangeCount, view.Count);

        // every key the view enumerates is in range and present in the parent
        long viaView = 0;
        foreach (var kv in view)
        {
            Assert.InRange(kv.Key, lo, hi - 1);
            Assert.True(d.TryGetValue(kv.Key, out var v) && v == kv.Value);
            viaView++;
        }
        Assert.Equal(inRangeCount, viaView);
    }
}
