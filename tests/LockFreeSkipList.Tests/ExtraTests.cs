using Mobratil.Collections;
using Xunit;
using Xunit.Abstractions;

namespace LockFreeSkipList.Tests;

public class ExtraTests
{
    private readonly ITestOutputHelper _out;
    public ExtraTests(ITestOutputHelper output) => _out = output;

    private static int Threads => Math.Max(4, Environment.ProcessorCount);

    [Fact]
    public void Clear_Empties_The_Dictionary()
    {
        var m = new ConcurrentSkipListDictionary<int, int>();
        for (int i = 0; i < 1000; i++) m[i] = i;
        Assert.Equal(1000, m.Count);
        m.Clear();
        Assert.True(m.IsEmpty);
        Assert.Equal(0, m.Count);
        Assert.False(m.ContainsKey(500));
        // Reusable after clear.
        m[7] = 7;
        Assert.Equal(1, m.Count);
        Assert.True(m.ContainsKey(7));
    }

    [Fact]
    public void Clear_Under_Concurrent_Writers_Never_Corrupts()
    {
        var m = new ConcurrentSkipListDictionary<int, int>();
        using var stop = new CancellationTokenSource();
        var workers = new List<Task>();
        for (int t = 0; t < Threads; t++)
        {
            int seed = t;
            workers.Add(Task.Run(() =>
            {
                var rng = new Random(seed * 7 + 1);
                while (!stop.IsCancellationRequested)
                {
                    int k = rng.Next(5000);
                    switch (rng.Next(3))
                    {
                        case 0: m[k] = k; break;
                        case 1: m.TryRemove(k, out _); break;
                        default: m.TryGetValue(k, out _); break;
                    }
                }
            }));
        }

        // Periodically clear; each enumeration must stay strictly sorted.
        var deadline = DateTime.UtcNow.AddSeconds(2);
        long clears = 0;
        while (DateTime.UtcNow < deadline)
        {
            m.Clear();
            int prev = int.MinValue;
            foreach (var kv in m)
            {
                Assert.True(kv.Key > prev);
                prev = kv.Key;
            }
            clears++;
        }
        stop.Cancel();
        Task.WaitAll(workers.ToArray());
        _out.WriteLine($"Performed {clears} concurrent clears with no corruption.");
        Assert.True(clears > 0);
    }

    /// <summary>
    /// Tiny key space hammered by many threads with add/remove — this maximises
    /// concurrent deletions on the same nodes, exercising the marker + helping
    /// machinery hardest. After the run, the structure must still be a valid,
    /// strictly-sorted set and every present key must be readable.
    /// </summary>
    [Fact]
    public void Hot_Small_Keyset_Exercises_Markers_And_Helping()
    {
        var m = new ConcurrentSkipListDictionary<int, int>();
        const int keys = 64;
        int threads = Threads;

        Parallel.For(0, threads, t =>
        {
            var rng = new Random(t * 13 + 9);
            for (int i = 0; i < 500_000; i++)
            {
                int k = rng.Next(keys);
                switch (rng.Next(3))
                {
                    case 0: m.TryAdd(k, k); break;
                    case 1: m.TryRemove(k, out _); break;
                    default:
                        if (m.TryGetValue(k, out var v)) Assert.Equal(k, v);
                        break;
                }
            }
        });

        // Invariants: sorted, unique, values consistent, count matches enumeration.
        int prev = int.MinValue, enumerated = 0;
        foreach (var kv in m)
        {
            Assert.True(kv.Key > prev, $"order/uniqueness violated: {kv.Key} after {prev}");
            Assert.True(kv.Key >= 0 && kv.Key < keys);
            Assert.Equal(kv.Key, kv.Value);
            prev = kv.Key;
            enumerated++;
        }
        Assert.Equal(enumerated, m.Count);

        // ContainsKey must agree with enumeration for every possible key.
        var present = new HashSet<int>(m.Keys);
        for (int k = 0; k < keys; k++)
            Assert.Equal(present.Contains(k), m.ContainsKey(k));
    }

    /// <summary>
    /// GetOrAdd from many threads on the same keys must yield a single agreed value
    /// per key (the first writer's), and never lose a key.
    /// </summary>
    [Fact]
    public void GetOrAdd_Is_Atomic_Under_Contention()
    {
        var m = new ConcurrentSkipListDictionary<int, int>();
        const int keys = 10_000;
        int threads = Threads;
        var results = new int[threads][];

        Parallel.For(0, threads, t =>
        {
            var local = new int[keys];
            for (int k = 0; k < keys; k++)
                local[k] = m.GetOrAdd(k, t * 1_000_000 + k); // distinct candidate per thread
            results[t] = local;
        });

        // Every thread must observe the SAME winning value for each key.
        for (int k = 0; k < keys; k++)
        {
            int agreed = results[0][k];
            for (int t = 1; t < threads; t++)
                Assert.Equal(agreed, results[t][k]);
            Assert.True(m.TryGetValue(k, out var stored));
            Assert.Equal(agreed, stored);
        }
        Assert.Equal(keys, m.Count);
    }
}
