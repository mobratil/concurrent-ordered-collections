using Ordered;
using Xunit;
using Xunit.Abstractions;

namespace LockFreeSkipList.Tests;

/// <summary>Balance/structure verification and parallel-chaos stress for the concurrent B+-tree.</summary>
public class ConcurrentBPlusTreeChaosTests
{
    private readonly ITestOutputHelper _out;
    public ConcurrentBPlusTreeChaosTests(ITestOutputHelper output) => _out = output;

    private static int Threads => Math.Max(4, Environment.ProcessorCount);

    // ---------- balance ----------

    [Theory]
    [InlineData(4)]
    [InlineData(8)]
    [InlineData(64)]
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
            var t = new ConcurrentBTreeDictionary<int, int>(order);
            foreach (var k in keys) t[k] = k;
            t.Validate();                               // all leaves same depth -> balanced
            var (depth, internals, leaves, fill) = t.DebugStats();
            // depth must be logarithmic, never linear
            int maxDepth = (int)(3 * Math.Log(n) / Math.Log(order)) + 3;
            Assert.True(depth <= maxDepth, $"{name}: depth {depth} > {maxDepth} (not balanced!)");
            Assert.Equal(n, t.Count);
            _out.WriteLine($"order={order} {name}: depth={depth} internals={internals} leaves={leaves} fill={fill:F0}%");
        }
    }

    [Fact]
    public void Heavy_Churn_Stays_Balanced_And_Reclaims_Drained_Leaves()
    {
        var t = new ConcurrentBTreeDictionary<int, int>(order: 32);
        var model = new SortedDictionary<int, int>();
        var rng = new Random(12345);
        const int keySpace = 200_000;

        // many rounds of churn
        for (int round = 0; round < 8; round++)
        {
            for (int i = 0; i < 60_000; i++)
            {
                int k = rng.Next(keySpace);
                if ((rng.Next() & 1) == 0) { t[k] = k; model[k] = k; }
                else { Assert.Equal(model.Remove(k), t.TryRemove(k, out _)); }
            }
            t.Validate();                                   // balanced + sorted + count-consistent every round
            Assert.Equal(model.Count, t.Count);
            Assert.Equal(model.ToList(), t.ToList());
        }

        var (_, _, leavesBefore, _) = t.DebugStats();

        // Drain almost everything, keeping only the 500 smallest keys. With empty-leaf reclamation,
        // every leaf to the right of the survivors is unlinked the moment it empties — so the leaf
        // count must collapse to roughly {survivors / fill}, not stay at the churn-time peak.
        var keep = new HashSet<int>(model.Keys.Take(500));
        foreach (var k in model.Keys.ToList()) if (!keep.Contains(k)) t.TryRemove(k, out _);
        foreach (var k in model.Keys.ToList()) if (!keep.Contains(k)) model.Remove(k);

        t.Validate();                                       // still balanced + chain-consistent AFTER reclamation
        Assert.Equal(model.Count, t.Count);
        Assert.Equal(model.ToList(), t.ToList());           // every survivor still present, in order

        var (depth, internals, leaves, fill) = t.DebugStats();
        _out.WriteLine($"drained {leavesBefore} -> {leaves} leaves for {t.Count} keys: depth={depth} internals={internals} fill={fill:F1}%");

        // The proof: recursive merge collapses the tree back down to track the surviving ~500 keys —
        // a few dozen well-filled leaves and a shallow depth, not the churn-time peak.
        Assert.True(leaves < 50, $"reclamation weak: {leaves} leaves still live for {t.Count} keys");
        Assert.True(fill > 50, $"leaves underfilled after merge: {fill:F1}%");
        Assert.True(depth <= 1, $"expected a shallow tree after draining to {t.Count} keys, depth={depth}");
    }

    // ---------- space reclamation ----------

    [Theory]
    [InlineData(32)]
    [InlineData(64)]
    public void Draining_A_Range_Reclaims_Leaves_Deterministically(int order)
    {
        var t = new ConcurrentBTreeDictionary<int, int>(order);
        const int n = 50_000;
        for (int k = 0; k < n; k++) t[k] = k;
        var (_, _, leavesFull, _) = t.DebugStats();

        // Delete the top 90% of the key range. Those leaves empty and (since their parents keep
        // sibling survivors at the boundary) get unlinked.
        for (int k = n / 10; k < n; k++) Assert.True(t.TryRemove(k, out _));
        t.Validate();
        Assert.Equal(n / 10, t.Count);

        var (_, _, leavesAfter, _) = t.DebugStats();
        Assert.True(leavesAfter < leavesFull / 5, $"order {order}: kept {leavesAfter}/{leavesFull} leaves for 10% of keys");
        // survivors intact and ordered
        Assert.Equal(Enumerable.Range(0, n / 10).ToList(), t.Keys.ToList());
    }

    [Theory]
    [InlineData(32)]
    [InlineData(64)]
    public void Scattered_Deletes_Reclaim_Via_Merge_Not_Just_Empty_Leaves(int order)
    {
        // The case empty-leaf unlink can't touch: delete a RANDOM 90%, so almost no leaf empties
        // outright — they just go underfull. Sibling merge must coalesce them back toward half-full,
        // so leaf fill recovers (without merge it would sit at ~10%).
        var t = new ConcurrentBTreeDictionary<int, int>(order);
        const int n = 100_000;
        for (int k = 0; k < n; k++) t[k] = k;
        var (_, _, leavesFull, fillFull) = t.DebugStats();

        var rng = new Random(20260605);
        var survivors = new SortedSet<int>();
        for (int k = 0; k < n; k++) { if (rng.Next(10) == 0) survivors.Add(k); else t.TryRemove(k, out _); }

        t.Validate();                                          // balanced + chain-consistent after scattered merge
        Assert.Equal(survivors.Count, t.Count);
        Assert.Equal(survivors.ToList(), t.Keys.ToList());     // exact survivors, in order

        var (_, _, leavesAfter, fillAfter) = t.DebugStats();
        _out.WriteLine($"order={order} scattered-drain: {leavesFull} leaves @ {fillFull:F0}% -> {leavesAfter} leaves @ {fillAfter:F0}% for {t.Count} keys");
        // Merge still coalesces well above the ~10% live fraction that lazy-delete-without-merge gives.
        // (The trigger is now order/3 — lazier than half-full, to avoid split/merge thrash — so fill sits
        // a bit lower than before but the structure still tracks the live set, not the build-time peak.)
        Assert.True(fillAfter > 25, $"merge didn't coalesce: fill only {fillAfter:F1}%");
        Assert.True(leavesAfter < leavesFull / 3, $"expected far fewer leaves, got {leavesAfter}/{leavesFull}");
    }

    [Theory]
    [InlineData(4)]
    [InlineData(64)]
    public void Full_Drain_Collapses_Completely_And_Is_Reusable(int order)
    {
        // Recursive merge eliminates the skeleton entirely: draining the whole tree cascades merges
        // level by level and collapses single-child roots, returning to a single-leaf root (depth 0) —
        // regardless of how deep the tree got (order 4 builds a depth-8 tree of 100k keys).
        var t = new ConcurrentBTreeDictionary<int, int>(order);
        for (int k = 0; k < 100_000; k++) t[k] = k;
        int builtDepth = t.DebugStats().Depth;
        Assert.True(builtDepth >= 1);

        for (int k = 0; k < 100_000; k++) t.TryRemove(k, out _);
        t.Validate();
        Assert.True(t.IsEmpty);

        var (depth, internals, leaves, _) = t.DebugStats();
        _out.WriteLine($"full drain (order {order}): built depth={builtDepth} -> after drain depth={depth} internals={internals} leaves={leaves}");
        Assert.Equal(0, depth);                              // single-leaf root — height fully reclaimed
        Assert.Equal(0, internals);                          // no skeleton internals remain
        Assert.Equal(1, leaves);

        // and it still works afterwards
        t[42] = 42;
        Assert.True(t.TryGetValue(42, out var v) && v == 42);
    }

    [Fact]
    public void Concurrent_Drain_Reclaims_While_Staying_Correct()
    {
        var t = new ConcurrentBTreeDictionary<long, long>(order: 16);
        int threads = Threads;
        const int partition = 40_000;
        // fill: each thread owns a disjoint contiguous block
        Parallel.For(0, threads, tid =>
        {
            long lo = (long)tid * partition;
            for (long k = lo; k < lo + partition; k++) t[k] = k;
        });
        t.Validate();
        var (_, _, leavesFull, _) = t.DebugStats();

        // concurrently drain everything except each block's first 10 keys
        Parallel.For(0, threads, tid =>
        {
            long lo = (long)tid * partition;
            for (long k = lo + 10; k < lo + partition; k++) Assert.True(t.TryRemove(k, out _));
        });

        t.Validate();                                          // balanced + chain-consistent after concurrent reclaim
        Assert.Equal((long)threads * 10, t.Count);
        var (_, _, leavesAfter, _) = t.DebugStats();
        _out.WriteLine($"concurrent drain {leavesFull} -> {leavesAfter} leaves for {t.Count} keys");
        Assert.True(leavesAfter < leavesFull / 4, $"reclaim under concurrency weak: {leavesAfter}/{leavesFull}");
        // every survivor present and findable
        for (int tid = 0; tid < threads; tid++)
            for (long k = (long)tid * partition; k < (long)tid * partition + 10; k++)
                Assert.True(t.TryGetValue(k, out var v) && v == k, $"lost survivor {k}");
    }

    /// <summary>Hammer the recursive (pessimistic, multi-level) merge: tiny order -> deep tree -> every
    /// other delete cascades a merge up several levels, concurrently with splits from other threads.
    /// Each thread owns a disjoint partition so every op has a deterministic expected result; at the end
    /// the union must equal the tree and the tree must be balanced and shallow (logarithmic depth).</summary>
    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(6)]
    public void Concurrent_Cascade_Stress_Tiny_Order(int order)
    {
        var t = new ConcurrentBTreeDictionary<long, long>(order);
        int threads = Threads;
        const int partition = 30_000;
        var models = new HashSet<long>[threads];

        Parallel.For(0, threads, tid =>
        {
            long lo = (long)tid * partition;
            var local = new HashSet<long>();
            var rng = new Random(tid * 7919 + 1);
            for (int i = 0; i < 200_000; i++)
            {
                long key = lo + rng.Next(partition);
                if ((rng.Next() & 1) == 0)
                    Assert.Equal(local.Add(key), t.TryAdd(key, key));      // disjoint -> must agree
                else
                    Assert.Equal(local.Remove(key), t.TryRemove(key, out _));
            }
            models[tid] = local;
        });

        long expected = models.Sum(m => (long)m.Count);
        Assert.Equal(expected, t.Count);
        t.Validate();                                                      // balanced + chain + count after all cascades

        long seen = 0, prev = long.MinValue;
        foreach (var kv in t)
        {
            Assert.True(kv.Key > prev, $"out of order {kv.Key} after {prev}"); prev = kv.Key;
            int owner = (int)(kv.Key / partition);
            Assert.True(models[owner].Contains(kv.Key), $"phantom key {kv.Key}");
            seen++;
        }
        Assert.Equal(expected, seen);

        // depth must stay logarithmic — proof the tree didn't degrade into a stick under churn
        int depth = t.DebugStats().Depth;
        int maxDepth = (int)(3 * Math.Log(Math.Max(2, expected)) / Math.Log(order)) + 4;
        Assert.True(depth <= maxDepth, $"order {order}: depth {depth} > {maxDepth} for {expected} keys");
    }

    // ---------- parallel chaos ----------

    /// <summary>
    /// GOLD STANDARD concurrency check: each thread owns a DISJOINT key partition and mirrors its
    /// own ops in a local HashSet. Because partitions don't overlap, every single operation has a
    /// deterministic expected result even while all threads concurrently split/grow the shared tree
    /// — so a per-op mismatch means another partition's activity corrupted this one. At the end the
    /// union of local models must equal the tree exactly, and the tree must be balanced.
    /// </summary>
    [Fact]
    public void Concurrent_Disjoint_Chaos_With_Local_Models()
    {
        var t = new ConcurrentBTreeDictionary<long, long>(order: 16);   // small order -> heavy split traffic
        int threads = Threads;
        const int partition = 50_000;
        var models = new HashSet<long>[threads];

        Parallel.For(0, threads, tid =>
        {
            long lo = (long)tid * partition;
            var local = new HashSet<long>();
            var rng = new Random(tid * 7919 + 1);
            for (int i = 0; i < 300_000; i++)
            {
                long key = lo + rng.Next(partition);
                if ((rng.Next() & 1) == 0)
                {
                    bool treeAdded = t.TryAdd(key, key);
                    bool modelAdded = local.Add(key);
                    Assert.Equal(modelAdded, treeAdded);          // disjoint -> must agree every time
                    if (treeAdded) Assert.True(t.TryGetValue(key, out var v) && v == key);
                }
                else
                {
                    bool treeRem = t.TryRemove(key, out _);
                    bool modelRem = local.Remove(key);
                    Assert.Equal(modelRem, treeRem);
                }
            }
            models[tid] = local;
        });

        // union of models == tree
        long expected = models.Sum(m => (long)m.Count);
        Assert.Equal(expected, t.Count);
        t.Validate();                                            // balanced + sorted after all that churn
        foreach (var m in models)
            foreach (var k in m)
                Assert.True(t.TryGetValue(k, out var v) && v == k, $"missing key {k}");
        // and the tree has nothing extra
        long seen = 0; long prev = long.MinValue;
        foreach (var kv in t)
        {
            Assert.True(kv.Key > prev); prev = kv.Key;
            int owner = (int)(kv.Key / partition);
            Assert.True(owner >= 0 && owner < threads && models[owner].Contains(kv.Key), $"phantom key {kv.Key}");
            seen++;
        }
        Assert.Equal(expected, seen);
    }

    /// <summary>
    /// Overlapping-key chaos: all threads hammer the SAME key space with random add/remove/update/get
    /// for a while. The final set is non-deterministic, but the structure must remain a valid, balanced
    /// B+-tree, and every enumerated key must be findable and in order.
    /// </summary>
    [Fact]
    public void Concurrent_Overlapping_Chaos_Then_Structure_Is_Valid()
    {
        var t = new ConcurrentBTreeDictionary<int, int>(order: 8);
        const int keySpace = 40_000;
        for (int k = 0; k < keySpace; k += 3) t[k] = k;          // seed

        using var stop = new CancellationTokenSource();
        var workers = new List<Task>();
        for (int w = 0; w < Threads; w++)
        {
            int seed = w;
            workers.Add(Task.Run(() =>
            {
                var rng = new Random(seed * 104729 + 17);
                while (!stop.IsCancellationRequested)
                {
                    int k = rng.Next(keySpace);
                    switch (rng.Next(4))
                    {
                        case 0: t.TryAdd(k, k); break;
                        case 1: t[k] = k + 1; break;            // update
                        case 2: t.TryRemove(k, out _); break;
                        default: t.TryGetValue(k, out _); break;
                    }
                }
            }));
        }
        Thread.Sleep(2500);
        stop.Cancel();
        Task.WaitAll(workers.ToArray());

        // quiescent structural verification
        t.Validate();
        int prev = int.MinValue, n = 0;
        foreach (var kv in t)
        {
            Assert.True(kv.Key > prev, $"out of order {kv.Key} after {prev}");
            Assert.True(t.TryGetValue(kv.Key, out var v) && (v == kv.Key || v == kv.Key + 1));
            prev = kv.Key; n++;
        }
        Assert.Equal(n, t.Count);
        var (depth, _, leaves, _) = t.DebugStats();
        _out.WriteLine($"after overlapping chaos: {t.Count} keys, depth={depth}, leaves={leaves}");
    }
}
