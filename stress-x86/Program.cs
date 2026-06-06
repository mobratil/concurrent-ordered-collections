using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Mobratil.Collections;

// Headless concurrency stress for ConcurrentBTreeDictionary, reproducing the two bugs the xUnit suite found
// and fixed: (1) the descent NullReferenceException and (2) the torn (key,value) range scan. Purpose:
// confirm the structure is clean on THIS architecture and report whether the ARM-only Validate fence is
// active here. Exit code 0 = all clean, 1 = a failure was detected. Env: STRESS_SECONDS (default 3),
// STRESS_ROUNDS (default 6), STRESS_ORDERS (default "4,8,64").
static class Stress
{
    static int Threads = Math.Max(8, Environment.ProcessorCount * 2);
    static int Seconds = Env("STRESS_SECONDS", 3);
    static int Rounds = Env("STRESS_ROUNDS", 6);
    static volatile bool Bad;
    static readonly List<string> Failures = new();

    static int Env(string k, int d) => int.TryParse(Environment.GetEnvironmentVariable(k), out var v) && v > 0 ? v : d;
    static void Fail(string m) { lock (Failures) { Failures.Add(m); } Bad = true; }

    static int Main()
    {
        Console.WriteLine($"# arch={RuntimeInformation.ProcessArchitecture} cores={Environment.ProcessorCount} threads={Threads} sec={Seconds} rounds={Rounds}");
        Console.WriteLine($"# NOTE: the Validate() LoadLoad fence is gated ON only for Arm/Arm64; on this arch it is " +
                          $"{(RuntimeInformation.ProcessArchitecture is Architecture.Arm64 or Architecture.Arm ? "ACTIVE" : "GATED OFF")}.");
        var orders = new List<int>();
        foreach (var s in (Environment.GetEnvironmentVariable("STRESS_ORDERS") ?? "4,8,64").Split(',')) orders.Add(int.Parse(s.Trim()));

        var sw = Stopwatch.StartNew();
        for (int r = 0; r < Rounds && !Bad; r++)
            foreach (var order in orders)
            {
                DescentAndCountChurn(order);          // NRE repro + quiescent count/dup/value reconcile
                ScanValueConsistency(order);          // torn (key,value) scan repro
                Console.WriteLine($"round {r} order {order}: ok ({sw.Elapsed.TotalSeconds:F0}s)");
                if (Bad) break;
            }

        if (Bad)
        {
            Console.WriteLine($"\n!!! {Failures.Count} FAILURE(S):");
            foreach (var f in Failures) Console.WriteLine("  - " + f);
            return 1;
        }
        Console.WriteLine("\nALL CLEAN");
        return 0;
    }

    // Tiny hot keyset hammered by oversubscribed insert/remove + concurrent gets: reproduces the descent NRE.
    static void DescentAndCountChurn(int order)
    {
        var d = new ConcurrentBTreeDictionary<int, int>(order);
        const int hot = 256;
        using var stop = new CancellationTokenSource();
        var tasks = new List<Task>();
        for (int t = 0; t < Threads; t++)
        {
            int id = t;
            tasks.Add(Task.Factory.StartNew(() =>
            {
                var rng = new Random(id * 37 + 13);
                try
                {
                    while (!stop.IsCancellationRequested)
                    {
                        int k = rng.Next(hot);
                        if ((rng.Next() & 1) == 0) d[k] = k; else d.TryRemove(k, out _);
                        d.TryGetValue(rng.Next(hot), out _);
                        if (d.TryGetCeiling(rng.Next(hot), out var e) && e.Key < 0) Fail("ceiling<0");
                    }
                }
                catch (Exception ex) { Fail($"order {order} EXCEPTION in churn: {ex.GetType().Name}: {ex.Message}"); }
            }, TaskCreationOptions.LongRunning));
        }
        Thread.Sleep(Seconds * 1000);
        stop.Cancel();
        Task.WaitAll(tasks.ToArray());

        // quiescent reconcile
        int prev = int.MinValue, enumerated = 0;
        foreach (var kv in d)
        {
            if (kv.Key <= prev) Fail($"order {order} dup/order in hot set: {kv.Key} after {prev}");
            if (kv.Key != kv.Value) Fail($"order {order} value!=key after quiesce: {kv.Key}->{kv.Value}");
            prev = kv.Key; enumerated++;
        }
        if (enumerated != d.Count) Fail($"order {order} COUNT DRIFT: enumerated {enumerated} but Count {d.Count}");
    }

    // 50k keys, churn, repeated whole-map scans asserting value==key + strictly ascending: torn-read repro.
    static void ScanValueConsistency(int order)
    {
        var d = new ConcurrentBTreeDictionary<int, int>(order);
        const int n = 50_000;
        Parallel.For(0, Environment.ProcessorCount, p => { for (int k = p; k < n; k += Environment.ProcessorCount) d[k] = k; });

        using var stop = new CancellationTokenSource();
        var tasks = new List<Task>();
        for (int t = 0; t < Threads; t++)
        {
            int id = t;
            tasks.Add(Task.Factory.StartNew(() =>
            {
                var rng = new Random(id * 7 + 1);
                while (!stop.IsCancellationRequested)
                {
                    int k = rng.Next(n);
                    if ((rng.Next() & 1) == 0) d[k] = k; else d.TryRemove(k, out _);
                }
            }, TaskCreationOptions.LongRunning));
        }
        var end = Stopwatch.StartNew();
        while (end.Elapsed.TotalSeconds < Seconds && !Bad)
        {
            int prev = int.MinValue;
            try
            {
                foreach (var kv in d)
                {
                    if (kv.Key <= prev) { Fail($"order {order} SCAN out-of-order/dup: {kv.Key} after {prev}"); break; }
                    if (kv.Key != kv.Value) { Fail($"order {order} TORN scan: {kv.Key}->{kv.Value}"); break; }
                    prev = kv.Key;
                }
            }
            catch (Exception ex) { Fail($"order {order} EXCEPTION in scan: {ex.GetType().Name}: {ex.Message}"); break; }
        }
        stop.Cancel();
        Task.WaitAll(tasks.ToArray());
    }
}
