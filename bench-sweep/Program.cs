using System;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using LockFree;
using Ordered;

// Headless scaling sweep -> CSV on stdout. Compares skip list vs OLC ConcurrentBTreeDictionary vs B-link, across
// thread counts, for three modes (read-only, write-only, mixed). Best-of-N per cell. Designed for a
// many-core Linux box; run under numactl to get single-node vs spanning. Env knobs:
//   SWEEP_THREADS   comma list of thread counts (default "1,2,4,8,16,32,48,64")
//   SWEEP_SECONDS   measured seconds per cell (default 2)
//   SWEEP_ITERS     best-of iterations (default 2)
//   SWEEP_PREFILL   prefill key count (default 1000000)
//   SWEEP_LABEL     a tag column (e.g. "all" / "node0") to mark the numactl context
class Sweep
{
    const int Order = 64;

    static long Range, Prefill;
    static int Seconds, Iters;
    static string Label = "";

    interface IMap { void Put(long k); bool Get(long k); void Rem(long k); }
    sealed class SkipMap : IMap { readonly ConcurrentSkipListDictionary<long, long> m = new(); public void Put(long k) => m[k] = k; public bool Get(long k) => m.TryGetValue(k, out _); public void Rem(long k) => m.TryRemove(k, out _); }
    sealed class BTreeMap : IMap { readonly ConcurrentBTreeDictionary<long, long> m = new(Order); public void Put(long k) => m[k] = k; public bool Get(long k) => m.TryGetValue(k, out _); public void Rem(long k) => m.TryRemove(k, out _); }
    sealed class BLinkMap : IMap { readonly BLinkTree<long, long> m = new(Order); public void Put(long k) => m[k] = k; public bool Get(long k) => m.TryGetValue(k, out _); public void Rem(long k) => m.TryRemove(k, out _); }

    // mode: 0 read-only, 1 write-only (50/50 put/remove), 2 mixed (half threads read, half write)
    static double Measure(Func<IMap> make, int threads, int mode)
    {
        double best = 0;
        for (int it = 0; it < Iters; it++)
        {
            var map = make();
            // Parallel prefill: even keys, interleaved across all cores (setup cost only, not measured).
            int pf = Environment.ProcessorCount;
            Parallel.For(0, pf, p => { for (long k = p; k < Prefill; k += pf) map.Put(k * 2); });
            if (it == 0)                                                  // one-shot RAM probe per cell
            {
                long mem = GC.GetTotalMemory(true);
                Console.Error.WriteLine($"# mem {Label} {map.GetType().Name} thr={threads} mode={mode} prefill={Prefill} heapMB={mem / 1048576.0:F0}");
            }
            long ops = 0;
            using var stop = new CancellationTokenSource();
            var tasks = new List<Task>(threads);
            for (int t = 0; t < threads; t++)
            {
                int id = t;
                bool reader = mode == 0 || (mode == 2 && (t % 2 == 0));
                tasks.Add(Task.Factory.StartNew(() =>
                {
                    var rng = new Random(id * 9176 + 1);
                    long n = 0;
                    if (reader) { while (!stop.IsCancellationRequested) { map.Get(rng.Next((int)Range)); n++; } }
                    else { while (!stop.IsCancellationRequested) { long k = rng.Next((int)Range); if ((rng.Next() & 1) == 0) map.Put(k); else map.Rem(k); n++; } }
                    Interlocked.Add(ref ops, n);
                }, TaskCreationOptions.LongRunning));
            }
            var sw = Stopwatch.StartNew();
            Thread.Sleep(Seconds * 1000);
            stop.Cancel();
            Task.WaitAll(tasks.ToArray());
            sw.Stop();
            double mops = ops / sw.Elapsed.TotalSeconds / 1e6;
            if (mops > best) best = mops;
        }
        return best;
    }

    static void Main()
    {
        string Env(string k, string d) => Environment.GetEnvironmentVariable(k) is { Length: > 0 } v ? v : d;
        Seconds = int.Parse(Env("SWEEP_SECONDS", "2"));
        Iters = int.Parse(Env("SWEEP_ITERS", "2"));
        Prefill = long.Parse(Env("SWEEP_PREFILL", "1000000"));
        Range = Prefill * 2;
        Label = Env("SWEEP_LABEL", "all");
        var threadList = new List<int>();
        foreach (var s in Env("SWEEP_THREADS", "1,2,4,8,16,32,48,64").Split(',')) threadList.Add(int.Parse(s.Trim()));

        var ci = CultureInfo.InvariantCulture;
        Console.Error.WriteLine($"# sweep label={Label} cores={Environment.ProcessorCount} order={Order} prefill={Prefill} sec={Seconds} iters={Iters}");
        Console.WriteLine("label,structure,mode,threads,mops");
        var structs = new (string name, Func<IMap> mk)[] { ("skiplist", () => new SkipMap()), ("bptree", () => new BTreeMap()), ("blink", () => new BLinkMap()) };
        var modes = new (int id, string name)[] { (0, "read"), (1, "write"), (2, "mixed") };
        foreach (var (mid, mname) in modes)
            foreach (var (sname, mk) in structs)
                foreach (var th in threadList)
                {
                    double mops = Measure(mk, th, mid);
                    Console.WriteLine(string.Format(ci, "{0},{1},{2},{3},{4:F2}", Label, sname, mname, th, mops));
                    Console.Out.Flush();
                }
    }
}
