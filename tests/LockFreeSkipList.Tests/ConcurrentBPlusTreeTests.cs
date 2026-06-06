using System.Collections.Concurrent;
using Ordered;
using Xunit;
using Xunit.Abstractions;

namespace LockFreeSkipList.Tests;

/// <summary>Step 2 — concurrent B+-tree. First proven correct single-threaded (model-based vs
/// SortedDictionary), then under parallel load with deterministic final states.</summary>
public class ConcurrentBPlusTreeTests
{
    private readonly ITestOutputHelper _out;
    public ConcurrentBPlusTreeTests(ITestOutputHelper output) => _out = output;

    private static int Threads => Math.Max(4, Environment.ProcessorCount);

    // ---------- single-threaded correctness ----------

    [Fact]
    public void Empty_And_Roundtrip()
    {
        var t = new ConcurrentBTreeDictionary<int, string>(order: 4);
        Assert.True(t.IsEmpty);
        Assert.False(t.TryGetValue(1, out _));
        Assert.True(t.TryAdd(1, "a"));
        Assert.False(t.TryAdd(1, "b"));
        Assert.Equal("a", t[1]);
        t[1] = "c";
        Assert.Equal("c", t[1]);
        Assert.True(t.TryRemove(1, out var r) && r == "c");
        Assert.True(t.IsEmpty);
    }

    [Theory]
    [InlineData(3, 1)]
    [InlineData(4, 7)]
    [InlineData(8, 42)]
    [InlineData(64, 99)]
    public void Model_Based_Against_SortedDictionary(int order, int seed)
    {
        var rng = new Random(seed);
        var tree = new ConcurrentBTreeDictionary<int, int>(order);
        var model = new SortedDictionary<int, int>();
        const int keySpace = 400;

        for (int i = 0; i < 40_000; i++)
        {
            int key = rng.Next(keySpace);
            switch (rng.Next(4))
            {
                case 0: int v = rng.Next(); tree[key] = v; model[key] = v; break;
                case 1:
                    int v2 = rng.Next();
                    Assert.Equal(!model.ContainsKey(key), tree.TryAdd(key, v2));
                    if (!model.ContainsKey(key)) model[key] = v2;
                    break;
                case 2: Assert.Equal(model.Remove(key), tree.TryRemove(key, out _)); break;
                case 3:
                    bool g = tree.TryGetValue(key, out var gv);
                    bool mg = model.TryGetValue(key, out var mv);
                    Assert.Equal(mg, g);
                    if (mg) Assert.Equal(mv, gv);
                    break;
            }
        }
        Assert.Equal(model.Count, tree.Count);
        Assert.Equal(model.ToList(), tree.ToList());
        Assert.Equal(model.Keys.ToList(), tree.Keys.ToList());
    }

    // ---------- concurrent integrity (deterministic final states) ----------

    [Fact]
    public void Stress_Small_Order_Disjoint_Inserts_Repeated()
    {
        int threads = Threads;
        const int per = 12_000;
        for (int rep = 0; rep < 30; rep++)
        {
            var t = new ConcurrentBTreeDictionary<int, int>(order: 4);   // tiny order -> constant splits
            Parallel.For(0, threads, tid =>
            {
                int baseK = tid * per;
                var rng = new Random(tid * 131 + rep * 7 + 1);
                var ord = Enumerable.Range(0, per).ToArray();
                for (int i = ord.Length - 1; i > 0; i--) { int j = rng.Next(i + 1); (ord[i], ord[j]) = (ord[j], ord[i]); }
                foreach (var k in ord)
                    Assert.True(t.TryAdd(baseK + k, baseK + k), $"rep {rep}: TryAdd of unique key {baseK + k} returned false");
            });

            int total = threads * per;
            if (t.Count != total)
            {
                long leafKeys = t.CountLeafKeys();
                int actuallyMissing = 0;
                for (int k = 0; k < total; k++) if (!t.ContainsKey(k)) actuallyMissing++;
                _out.WriteLine($"rep {rep}: total={total} Count={t.Count} leafChainKeys={leafKeys} missingViaGet={actuallyMissing}");
            }
            Assert.Equal(total, t.Count);
            int prev = int.MinValue, n = 0;
            foreach (var kv in t)
            {
                Assert.True(kv.Key > prev, $"rep {rep}: order {kv.Key} after {prev}");
                Assert.Equal(kv.Key, kv.Value);
                prev = kv.Key; n++;
            }
            Assert.Equal(total, n);
            // every key present
            for (int k = 0; k < total; k++)
                Assert.True(t.TryGetValue(k, out var v) && v == k, $"rep {rep}: missing key {k}");
        }
    }

    [Fact]
    public void Disjoint_Parallel_Inserts_Preserve_All()
    {
        var t = new ConcurrentBTreeDictionary<long, long>(order: 32);
        const int perThread = 50_000;
        int threads = Threads;

        Parallel.For(0, threads, tid =>
        {
            long baseK = (long)tid * perThread;
            var rng = new Random(tid * 911 + 7);
            var order = Enumerable.Range(0, perThread).ToArray();
            for (int i = order.Length - 1; i > 0; i--) { int j = rng.Next(i + 1); (order[i], order[j]) = (order[j], order[i]); }
            foreach (var k in order) Assert.True(t.TryAdd(baseK + k, (baseK + k) * 2));
        });

        int total = threads * perThread;
        Assert.Equal(total, t.Count);
        long prev = long.MinValue, n = 0;
        foreach (var kv in t)
        {
            Assert.True(kv.Key > prev, $"order: {kv.Key} after {prev}");
            Assert.Equal(kv.Key * 2, kv.Value);
            prev = kv.Key; n++;
        }
        Assert.Equal(total, n);
        var rngc = new Random(7);
        for (int i = 0; i < 10_000; i++) { long k = rngc.Next(total); Assert.True(t.TryGetValue(k, out var vv) && vv == k * 2); }
    }

    [Fact]
    public void Concurrent_TryAdd_Same_Keys_Exactly_Once()
    {
        var t = new ConcurrentBTreeDictionary<int, int>(order: 16);
        const int keys = 50_000;
        long won = 0;
        Parallel.For(0, Threads, tid =>
        {
            long local = 0;
            var rng = new Random(tid * 31 + 1);
            var order = Enumerable.Range(0, keys).ToArray();
            for (int i = order.Length - 1; i > 0; i--) { int j = rng.Next(i + 1); (order[i], order[j]) = (order[j], order[i]); }
            foreach (var k in order) if (t.TryAdd(k, tid)) local++;
            Interlocked.Add(ref won, local);
        });
        Assert.Equal(keys, won);
        Assert.Equal(keys, t.Count);
        for (int k = 0; k < keys; k++) Assert.True(t.ContainsKey(k));
    }

    [Fact]
    public void Concurrent_Remove_Same_Keys_Exactly_Once()
    {
        var t = new ConcurrentBTreeDictionary<int, int>(order: 16);
        const int keys = 50_000;
        for (int k = 0; k < keys; k++) t[k] = k;
        long removed = 0;
        Parallel.For(0, Threads, tid =>
        {
            long local = 0;
            var rng = new Random(tid * 17 + 5);
            var order = Enumerable.Range(0, keys).ToArray();
            for (int i = order.Length - 1; i > 0; i--) { int j = rng.Next(i + 1); (order[i], order[j]) = (order[j], order[i]); }
            foreach (var k in order) if (t.TryRemove(k, out _)) local++;
            Interlocked.Add(ref removed, local);
        });
        Assert.Equal(keys, removed);
        Assert.Equal(0, t.Count);
        for (int k = 0; k < keys; k++) Assert.False(t.ContainsKey(k));
    }

    [Fact]
    public void Mixed_Churn_Then_Drain_Leaves_Exact_Survivors()
    {
        var t = new ConcurrentBTreeDictionary<int, int>(order: 32);
        const int keySpace = 100_000;
        for (int k = 0; k < keySpace; k += 2) t[k] = k;            // even survivors

        Parallel.For(0, Threads, tid =>
        {
            var rng = new Random(tid * 2654435761u.GetHashCode() ^ tid);
            for (int i = 0; i < 200_000; i++)
            {
                int key = rng.Next(keySpace) | 1;                  // odd churn keys
                if ((rng.Next() & 1) == 0) t[key] = key; else t.TryRemove(key, out _);
                if ((i & 63) == 0) t.TryGetValue(rng.Next(keySpace) & ~1, out _);
            }
        });

        Parallel.For(0, Threads, _ =>
        {
            for (int key = 1; key < keySpace; key += 2) t.TryRemove(key, out _);
        });

        int prev = int.MinValue, count = 0;
        foreach (var kv in t)
        {
            Assert.True((kv.Key & 1) == 0, $"odd survived: {kv.Key}");
            Assert.True(kv.Key > prev);
            Assert.Equal(kv.Key, kv.Value);
            prev = kv.Key; count++;
        }
        Assert.Equal(keySpace / 2, count);
        Assert.Equal(keySpace / 2, t.Count);
    }

    [Fact]
    public void Enumeration_Always_Sorted_Under_Concurrent_Mutation()
    {
        var t = new ConcurrentBTreeDictionary<int, int>(order: 16);
        const int keySpace = 30_000;
        for (int k = 0; k < keySpace; k += 2) t[k] = k;

        using var stop = new CancellationTokenSource();
        var writers = new List<Task>();
        for (int w = 0; w < Threads; w++)
        {
            int seed = w;
            writers.Add(Task.Run(() =>
            {
                var rng = new Random(seed * 101 + 3);
                while (!stop.IsCancellationRequested)
                {
                    int key = rng.Next(keySpace);
                    if ((rng.Next() & 1) == 0) t[key] = key; else t.TryRemove(key, out _);
                }
            }));
        }

        long enums = 0;
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (DateTime.UtcNow < deadline)
        {
            int prev = int.MinValue;
            foreach (var kv in t) { Assert.True(kv.Key > prev, $"not sorted: {kv.Key} after {prev}"); prev = kv.Key; }
            enums++;
        }
        stop.Cancel();
        Task.WaitAll(writers.ToArray());
        _out.WriteLine($"{enums} concurrent enumerations, all strictly ascending");
        Assert.True(enums > 0);
    }

    [Fact]
    public void Concurrent_Mixed_Vs_Lock_Protected_Oracle()
    {
        // Random concurrent ops on a small key space; each thread logs nothing, but a final
        // quiescent comparison against a lock-guarded SortedDictionary mirror checks the set.
        var t = new ConcurrentBTreeDictionary<int, int>(order: 8);
        var oracle = new SortedDictionary<int, int>();
        var gate = new object();
        const int keySpace = 2000;

        Parallel.For(0, Threads, tid =>
        {
            var rng = new Random(tid * 7919 + 13);
            for (int i = 0; i < 100_000; i++)
            {
                int key = rng.Next(keySpace);
                int val = key * 1000 + tid;
                // To keep the oracle and tree in lockstep, serialize each op under the gate.
                lock (gate)
                {
                    switch (rng.Next(3))
                    {
                        case 0: t[key] = val; oracle[key] = val; break;
                        case 1: Assert.Equal(oracle.Remove(key), t.TryRemove(key, out _)); break;
                        default:
                            Assert.Equal(oracle.TryGetValue(key, out var ov), t.TryGetValue(key, out var tv));
                            if (oracle.ContainsKey(key)) Assert.Equal(ov, tv);
                            break;
                    }
                }
            }
        });

        Assert.Equal(oracle.Count, t.Count);
        Assert.Equal(oracle.ToList(), t.ToList());
    }
}
