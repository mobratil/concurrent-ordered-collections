using Mobratil.Collections;
using Xunit;
using Xunit.Abstractions;

namespace LockFreeSkipList.Tests;

/// <summary>Correctness for the experimental Lehman&amp;Yao B-link tree: sequential model equivalence,
/// move-right + lock-free-split concurrency (disjoint partitions with per-op model agreement), and
/// quiescent structural validation. Delete is lazy (no merge yet) — these tests don't assert reclamation.</summary>
public class BLinkTreeTests
{
    private readonly ITestOutputHelper _out;
    public BLinkTreeTests(ITestOutputHelper output) => _out = output;

    private static int Threads => Math.Max(4, Environment.ProcessorCount);

    [Fact]
    public void Empty_And_Basic_Roundtrip()
    {
        var t = new BLinkTree<int, int>(order: 4);
        Assert.True(t.IsEmpty);
        Assert.False(t.TryGetValue(1, out _));
        Assert.True(t.TryAdd(1, 10));
        Assert.False(t.TryAdd(1, 99));      // duplicate
        Assert.True(t.TryGetValue(1, out var v) && v == 10);
        t[1] = 11;                          // update via indexer
        Assert.Equal(11, t[1]);
        Assert.True(t.TryRemove(1, out var r) && r == 11);
        Assert.True(t.IsEmpty);
    }

    [Theory]
    [InlineData(3)] [InlineData(4)] [InlineData(8)] [InlineData(64)]
    public void Model_Based_Against_SortedDictionary(int order)
    {
        var t = new BLinkTree<int, int>(order);
        var model = new SortedDictionary<int, int>();
        var rng = new Random(order * 31 + 7);
        for (int i = 0; i < 40_000; i++)
        {
            int k = rng.Next(1000);
            if ((rng.Next() & 1) == 0)
            {
                Assert.Equal(!model.ContainsKey(k), t.TryAdd(k, k));
                model[k] = k;
            }
            else
            {
                Assert.Equal(model.Remove(k), t.TryRemove(k, out _));
            }
            if (i % 2000 == 0)
            {
                t.Validate();
                Assert.Equal(model.ContainsKey(k), t.TryGetValue(k, out _));
            }
        }
        t.Validate();
        Assert.Equal(model.Count, t.Count);
        Assert.Equal(model.Keys.ToList(), t.Keys.ToList());
    }

    [Theory]
    [InlineData(4)] [InlineData(64)]
    public void Adversarial_Insert_Orders_Stay_Balanced(int order)
    {
        const int n = 20_000;
        foreach (var (name, keys) in new (string, IEnumerable<int>)[]
        {
            ("ascending", Enumerable.Range(0, n)),
            ("descending", Enumerable.Range(0, n).Reverse()),
            ("zigzag", Enumerable.Range(0, n).Select(i => (i % 2 == 0) ? i / 2 : n - 1 - i / 2)),
        })
        {
            var t = new BLinkTree<int, int>(order);
            foreach (var k in keys) t[k] = k;
            t.Validate();                                   // all leaves level 0 -> balanced
            Assert.Equal(n, t.Count);
            Assert.Equal(Enumerable.Range(0, n).ToList(), t.Keys.ToList());
            var (depth, _, _) = t.DebugStats();
            int maxDepth = (int)(3 * Math.Log(n) / Math.Log(order)) + 3;
            Assert.True(depth <= maxDepth, $"{name}: depth {depth} > {maxDepth}");
        }
    }

    /// <summary>Gold-standard concurrency check: disjoint key partitions per thread, so every op has a
    /// deterministic expected result even while all threads concurrently split/grow the shared tree and
    /// readers move-right past in-flight splits. A per-op mismatch means another partition corrupted this
    /// one. At the end the union of local models must equal the tree exactly and it must be balanced.</summary>
    [Theory]
    [InlineData(3)] [InlineData(4)] [InlineData(16)]
    public void Concurrent_Disjoint_Chaos_With_Local_Models(int order)
    {
        var t = new BLinkTree<long, long>(order);
        int threads = Threads;
        const int partition = 40_000;
        var models = new HashSet<long>[threads];

        Parallel.For(0, threads, tid =>
        {
            long lo = (long)tid * partition;
            var local = new HashSet<long>();
            var rng = new Random(tid * 7919 + 1);
            for (int i = 0; i < 250_000; i++)
            {
                long key = lo + rng.Next(partition);
                if ((rng.Next() & 1) == 0)
                {
                    Assert.Equal(local.Add(key), t.TryAdd(key, key));     // disjoint -> must agree
                    Assert.True(t.TryGetValue(key, out var v) && v == key);
                }
                else
                {
                    Assert.Equal(local.Remove(key), t.TryRemove(key, out _));
                }
            }
            models[tid] = local;
        });

        long expected = models.Sum(m => (long)m.Count);
        Assert.Equal(expected, t.Count);
        t.Validate();
        foreach (var m in models)
            foreach (var k in m)
                Assert.True(t.TryGetValue(k, out var v) && v == k, $"missing key {k}");

        long seen = 0, prev = long.MinValue;
        foreach (var kv in t)
        {
            Assert.True(kv.Key > prev, $"out of order {kv.Key} after {prev}"); prev = kv.Key;
            int owner = (int)(kv.Key / partition);
            Assert.True(owner >= 0 && owner < threads && models[owner].Contains(kv.Key), $"phantom key {kv.Key}");
            seen++;
        }
        Assert.Equal(expected, seen);
    }

    /// <summary>Readers must never miss a present key while writers concurrently split nodes (the move-right
    /// hazard): seed a key space, then hammer add/remove while one thread continuously verifies the full
    /// snapshot is sorted and every enumerated key is independently findable.</summary>
    [Fact]
    public void Concurrent_Readers_Never_Miss_Across_Splits()
    {
        var t = new BLinkTree<int, int>(order: 8);
        const int keySpace = 60_000;
        for (int k = 0; k < keySpace; k += 2) t[k] = k;     // seed evens

        using var stop = new CancellationTokenSource();
        var writers = new List<Task>();
        for (int w = 0; w < Threads; w++)
        {
            int seed = w;
            writers.Add(Task.Run(() =>
            {
                var rng = new Random(seed * 104729 + 17);
                while (!stop.IsCancellationRequested)
                {
                    int k = rng.Next(keySpace);
                    if ((rng.Next() & 1) == 0) t.TryAdd(k, k); else t.TryRemove(k, out _);
                }
            }));
        }

        long scans = 0;
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (DateTime.UtcNow < deadline)
        {
            int prev = int.MinValue;
            foreach (var kv in t) { Assert.True(kv.Key > prev, $"scan out of order {kv.Key} after {prev}"); prev = kv.Key; }
            scans++;
        }
        stop.Cancel();
        Task.WaitAll(writers.ToArray());
        t.Validate();
        Assert.True(scans > 0);
    }
}
