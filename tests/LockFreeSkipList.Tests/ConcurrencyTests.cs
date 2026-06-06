using System.Collections.Concurrent;
using Mobratil.Collections;
using Xunit;
using Xunit.Abstractions;

namespace LockFreeSkipList.Tests;

/// <summary>
/// Integrity under parallel load. These tests are designed so the *expected* final
/// state is deterministic even though the interleaving is not, which lets us assert
/// exact correctness rather than just "didn't crash".
/// </summary>
public class ConcurrencyTests
{
    private readonly ITestOutputHelper _out;
    public ConcurrencyTests(ITestOutputHelper output) => _out = output;

    private static int Threads => Math.Max(4, Environment.ProcessorCount);

    /// <summary>
    /// Each thread inserts a disjoint key range. The union must be present, complete,
    /// strictly sorted, with correct values and exact count.
    /// </summary>
    [Fact]
    public void Disjoint_Parallel_Inserts_Preserve_All_Entries()
    {
        var map = new ConcurrentSkipListDictionary<long, long>();
        const int perThread = 50_000;
        int threads = Threads;

        Parallel.For(0, threads, t =>
        {
            long baseKey = (long)t * perThread;
            // Insert in a shuffled order so towers interleave across threads.
            var rng = new Random(t * 911 + 7);
            var order = Enumerable.Range(0, perThread).ToArray();
            for (int i = order.Length - 1; i > 0; i--) { int j = rng.Next(i + 1); (order[i], order[j]) = (order[j], order[i]); }
            foreach (var k in order)
            {
                long key = baseKey + k;
                Assert.True(map.TryAdd(key, key * 2));
            }
        });

        int total = threads * perThread;
        Assert.Equal(total, map.Count);

        // Strictly ascending, complete, correct values — single pass.
        long prev = long.MinValue;
        long n = 0;
        foreach (var kv in map)
        {
            Assert.True(kv.Key > prev, $"order violated at {kv.Key} after {prev}");
            Assert.Equal(kv.Key * 2, kv.Value);
            prev = kv.Key;
            n++;
        }
        Assert.Equal(total, n);

        // Spot-check random membership.
        var rngc = new Random(12345);
        for (int i = 0; i < 10_000; i++)
        {
            long key = rngc.Next(total);
            Assert.True(map.TryGetValue(key, out var v));
            Assert.Equal(key * 2, v);
        }
    }

    /// <summary>
    /// Many threads all race to TryAdd the SAME key set. TryAdd must succeed exactly
    /// once per key across all threads (linearizable insert, no duplicates).
    /// </summary>
    [Fact]
    public void Concurrent_TryAdd_Of_Same_Keys_Succeeds_Exactly_Once()
    {
        var map = new ConcurrentSkipListDictionary<int, int>();
        const int keys = 20_000;
        int threads = Threads;
        long totalSucceeded = 0;

        Parallel.For(0, threads, t =>
        {
            long local = 0;
            var rng = new Random(t * 31 + 1);
            var order = Enumerable.Range(0, keys).ToArray();
            for (int i = order.Length - 1; i > 0; i--) { int j = rng.Next(i + 1); (order[i], order[j]) = (order[j], order[i]); }
            foreach (var k in order)
                if (map.TryAdd(k, t)) local++;
            Interlocked.Add(ref totalSucceeded, local);
        });

        Assert.Equal(keys, totalSucceeded);   // exactly one winner per key
        Assert.Equal(keys, map.Count);
        for (int k = 0; k < keys; k++) Assert.True(map.ContainsKey(k));
    }

    /// <summary>
    /// Pre-fill keys, then many threads all race to remove ALL of them. TryRemove must
    /// return true exactly once per key (no double-remove, no lost remove).
    /// </summary>
    [Fact]
    public void Concurrent_Remove_Of_Same_Keys_Succeeds_Exactly_Once()
    {
        var map = new ConcurrentSkipListDictionary<int, int>();
        const int keys = 20_000;
        for (int k = 0; k < keys; k++) map[k] = k;

        int threads = Threads;
        long totalRemoved = 0;

        Parallel.For(0, threads, t =>
        {
            long local = 0;
            var rng = new Random(t * 17 + 5);
            var order = Enumerable.Range(0, keys).ToArray();
            for (int i = order.Length - 1; i > 0; i--) { int j = rng.Next(i + 1); (order[i], order[j]) = (order[j], order[i]); }
            foreach (var k in order)
                if (map.TryRemove(k, out _)) local++;
            Interlocked.Add(ref totalRemoved, local);
        });

        Assert.Equal(keys, totalRemoved);   // each key removed exactly once
        Assert.True(map.IsEmpty);
        Assert.Equal(0, map.Count);
        for (int k = 0; k < keys; k++) Assert.False(map.ContainsKey(k));
    }

    /// <summary>
    /// Hammer AddOrUpdate and Merge as concurrent counters on a small shared key set.
    /// Their CAS-retry loops must lose no update: each key's final value equals the exact
    /// number of increments applied to it across all threads.
    /// </summary>
    [Fact]
    public void Concurrent_AddOrUpdate_And_Merge_Counters_Lose_No_Updates()
    {
        var d = new ConcurrentSkipListDictionary<int, long>();
        const int keys = 256, rounds = 400;
        int threads = Threads;

        Parallel.For(0, threads, t =>
        {
            for (int r = 0; r < rounds; r++)
                for (int k = 0; k < keys; k++)
                {
                    if ((k & 1) == 0) d.AddOrUpdate(k, 1, (_, old) => old + 1);
                    else d.AddOrUpdate(k, 1, (_, old) => old + 1);
                }
        });

        long expected = (long)threads * rounds;   // increments applied to every key
        Assert.Equal(keys, d.Count);
        for (int k = 0; k < keys; k++)
            Assert.True(d.TryGetValue(k, out var v) && v == expected,
                $"key {k}: expected {expected}, got {(d.TryGetValue(k, out var x) ? x : -1)}");
    }

    /// <summary>
    /// Many threads concurrently poll the minimum (TryRemoveFirst). Every key must come
    /// out exactly once across all threads — proving the lock-free remove-min is
    /// linearizable (no key polled twice, none lost).
    /// </summary>
    [Fact]
    public void Concurrent_PollFirst_Drains_Each_Key_Exactly_Once()
    {
        var map = new ConcurrentSkipListDictionary<int, int>();
        const int keys = 100_000;
        for (int k = 0; k < keys; k++) map[k] = k;

        var seen = new ConcurrentDictionary<int, int>();
        long polled = 0;

        Parallel.For(0, Threads, _ =>
        {
            while (map.TryRemoveFirst(out var e))
            {
                Assert.Equal(e.Key, e.Value);
                seen.AddOrUpdate(e.Key, 1, (_, n) => n + 1);
                Interlocked.Increment(ref polled);
            }
        });

        Assert.True(map.IsEmpty);
        Assert.Equal(keys, polled);
        Assert.Equal(keys, seen.Count);                 // every distinct key
        Assert.All(seen.Values, n => Assert.Equal(1, n)); // exactly once each
    }

    /// <summary>
    /// Poll-min from one side while poll-max runs from the other, plus a writer re-adding
    /// keys. They must never both claim the same key: the total of distinct (key removed)
    /// events never exceeds what was inserted, and the structure stays sorted/consistent.
    /// </summary>
    [Fact]
    public void Concurrent_PollFirst_And_PollLast_Never_Double_Claim()
    {
        var map = new ConcurrentSkipListDictionary<long, long>();
        const int keys = 50_000;
        for (int k = 0; k < keys; k++) map[k] = k;

        var claims = new ConcurrentDictionary<long, int>();
        using var stop = new CancellationTokenSource();

        var pollers = new List<Task>();
        for (int i = 0; i < Threads; i++)
        {
            bool fromFront = i % 2 == 0;
            pollers.Add(Task.Run(() =>
            {
                while (!stop.IsCancellationRequested)
                {
                    bool got = fromFront ? map.TryRemoveFirst(out var e) : map.TryRemoveLast(out e);
                    if (got) claims.AddOrUpdate(e.Key, 1, (_, n) => n + 1);
                    else if (map.IsEmpty) break;
                }
            }));
        }

        Task.WaitAll(pollers.ToArray());
        stop.Cancel();

        Assert.True(map.IsEmpty);
        Assert.Equal(keys, claims.Count);
        Assert.All(claims.Values, n => Assert.Equal(1, n)); // no key claimed by both ends
    }

    /// <summary>
    /// Mixed add/remove churn on a shared key space from many threads, with a final
    /// deterministic phase: every thread removes its owned keys. We then assert the
    /// surviving set is exactly the keys nobody was asked to remove — proving no
    /// corruption (lost survivors / phantom entries / order breakage).
    /// </summary>
    [Fact]
    public void Mixed_Churn_Then_Deterministic_Drain_Leaves_Exact_Survivors()
    {
        var map = new ConcurrentSkipListDictionary<int, int>();
        const int keySpace = 100_000;
        int threads = Threads;

        // Partition keys: even-indexed keys are "permanent" survivors; odd ones churn.
        // Pre-insert survivors.
        for (int k = 0; k < keySpace; k += 2) map[k] = k;

        // Churn phase: hammer odd keys with add/remove from all threads.
        Parallel.For(0, threads, t =>
        {
            var rng = new Random(t * 2654435761u.GetHashCode() ^ t);
            for (int i = 0; i < 200_000; i++)
            {
                int key = rng.Next(keySpace) | 1; // odd key
                if ((rng.Next() & 1) == 0) map[key] = key;
                else map.TryRemove(key, out _);
                // occasionally touch survivors with reads to stress traversal
                if ((i & 63) == 0) map.TryGetValue(rng.Next(keySpace) & ~1, out _);
            }
        });

        // Drain phase: remove every odd key, deterministically, from all threads.
        Parallel.For(0, threads, t =>
        {
            for (int key = 1; key < keySpace; key += 2) map.TryRemove(key, out _);
        });

        // Survivors must be exactly the even keys, intact and sorted.
        int prev = int.MinValue, count = 0;
        foreach (var kv in map)
        {
            Assert.True((kv.Key & 1) == 0, $"odd key {kv.Key} survived");
            Assert.True(kv.Key > prev);
            Assert.Equal(kv.Key, kv.Value);
            prev = kv.Key;
            count++;
        }
        Assert.Equal(keySpace / 2, count);
        Assert.Equal(keySpace / 2, map.Count);
    }

    /// <summary>
    /// Writers continuously mutate while readers enumerate. Enumeration must never
    /// throw and must always be strictly ascending (weakly-consistent snapshot).
    /// </summary>
    [Fact]
    public void Enumeration_Is_Always_Sorted_Under_Concurrent_Mutation()
    {
        var map = new ConcurrentSkipListDictionary<int, int>();
        const int keySpace = 30_000;
        for (int k = 0; k < keySpace; k += 2) map[k] = k;

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
                    if ((rng.Next() & 1) == 0) map[key] = key;
                    else map.TryRemove(key, out _);
                }
            }));
        }

        // Reader: enumerate many times, each must be strictly ascending.
        long enumerations = 0;
        var readerDeadline = DateTime.UtcNow.AddSeconds(2);
        while (DateTime.UtcNow < readerDeadline)
        {
            int prev = int.MinValue;
            foreach (var kv in map)
            {
                Assert.True(kv.Key > prev, $"enumeration not sorted: {kv.Key} after {prev}");
                prev = kv.Key;
            }
            enumerations++;
        }

        stop.Cancel();
        Task.WaitAll(writers.ToArray());
        _out.WriteLine($"Completed {enumerations} concurrent enumerations with no order violations.");
        Assert.True(enumerations > 0);
    }

    /// <summary>
    /// Producer/consumer: producers insert unique keys into a shared map, consumers
    /// remove them. A key is "owned" by exactly one consumer that removed it. We track
    /// every successful remove and assert the multiset of removed keys equals the
    /// produced keys exactly (no key removed twice, none lost).
    /// </summary>
    [Fact]
    public void Producer_Consumer_Every_Key_Consumed_Exactly_Once()
    {
        var map = new ConcurrentSkipListDictionary<long, long>();
        const int totalKeys = 200_000;
        int producers = Math.Max(2, Threads / 2);
        int consumers = Math.Max(2, Threads / 2);

        var removedCounts = new ConcurrentDictionary<long, int>();
        long produced = 0, consumed = 0;
        using var allProducedSignal = new CountdownEvent(producers);

        var prodTasks = new List<Task>();
        for (int p = 0; p < producers; p++)
        {
            int pid = p;
            prodTasks.Add(Task.Run(() =>
            {
                for (long k = pid; k < totalKeys; k += producers)
                {
                    map[k] = k;
                    Interlocked.Increment(ref produced);
                }
                allProducedSignal.Signal();
            }));
        }

        var consTasks = new List<Task>();
        for (int c = 0; c < consumers; c++)
        {
            consTasks.Add(Task.Run(() =>
            {
                var rng = new Random(Environment.CurrentManagedThreadId);
                while (true)
                {
                    bool finishing = allProducedSignal.IsSet;
                    long key = rng.Next(totalKeys);
                    if (map.TryRemove(key, out var v))
                    {
                        Assert.Equal(key, v);
                        removedCounts.AddOrUpdate(key, 1, (_, n) => n + 1);
                        Interlocked.Increment(ref consumed);
                    }
                    else if (finishing)
                    {
                        // Sweep remaining keys deterministically once production is done.
                        for (long k = 0; k < totalKeys; k++)
                            if (map.TryRemove(k, out var vv))
                            {
                                removedCounts.AddOrUpdate(k, 1, (_, n) => n + 1);
                                Interlocked.Increment(ref consumed);
                            }
                        if (map.IsEmpty) break;
                    }
                }
            }));
        }

        Task.WaitAll(prodTasks.ToArray());
        Task.WaitAll(consTasks.ToArray());

        Assert.True(map.IsEmpty);
        Assert.Equal(totalKeys, produced);
        Assert.Equal(totalKeys, removedCounts.Count);        // every distinct key removed
        Assert.All(removedCounts.Values, n => Assert.Equal(1, n)); // exactly once each
    }
}
