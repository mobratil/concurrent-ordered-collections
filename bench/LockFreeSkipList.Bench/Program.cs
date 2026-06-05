using System.Diagnostics;
using System.Globalization;
using System.Collections.Concurrent.Extended; // mkrebser ConcurrentSortedDictionary (MIT) — bench/ThirdParty
using LockFree;
using Ordered;

// =============================================================================
//  Cross-language benchmark harness (C# side).
//
//  Fairness design:
//   * Identical workload to the Java harness: same SplitMix64 PRNG, same seeds,
//     same key range, same operation mix, same pre-fill, same thread counts,
//     same warmup/measure iteration counts. Each thread therefore executes the
//     exact same operation stream in both runtimes.
//   * Server GC + concurrent GC enabled to match the JVM's throughput-oriented,
//     concurrent G1 collector.
//   * Threads start together behind a barrier; we measure pure steady-state.
//   * Two .NET variants, parameterised only by how the workload's `long` maps onto
//     the dictionary's element type T (no bespoke wrapper interface — the harness is
//     generic over the concrete LockFreeSkipListDictionary<T,T>):
//        long-long : value types, no boxing (idiomatic .NET)
//        ref-ref   : a reference-type wrapper (one heap object per put/get/remove),
//                    the .NET analogue of Java boxing long into java.lang.Long
//     The Java harness (java.util.concurrent.ConcurrentSkipListMap<Long,Long>) is the
//     boxing equivalent of "ref-ref".
// =============================================================================

static class Workload
{
    // Defaults chosen to be meaty but to finish in a reasonable wall-clock.
    public static int KeyRange      = 2_000_000;   // keys drawn from [0,KeyRange)
    public static int InitialKeys   = 1_000_000;   // pre-filled before measuring
    public static long OpsPerThread = 4_000_000;   // operations per worker thread
    public const int ReadPct        = 80;          // 80% get
    public const int PutPct         = 18;          // 18% put  (remove = 2%)
    public static int WarmupIters   = 3;
    public static int MeasureIters  = 5;
    public static int[] ThreadCounts = { 1, 2, 4, 8 };

    public static void Quick()
    {
        KeyRange = 200_000; InitialKeys = 100_000; OpsPerThread = 500_000;
        WarmupIters = 1; MeasureIters = 2;
    }

    // Cache-resident working set, but full rigour (warmup/iterations unchanged).
    // Here per-op allocation/boxing dominates over memory latency.
    public static void Small()
    {
        KeyRange = 200_000; InitialKeys = 100_000;
    }
}

// SplitMix64 — identical implementation must exist on the Java side.
struct SplitMix64
{
    private ulong _state;
    public SplitMix64(ulong seed) => _state = seed;

    public ulong Next()
    {
        unchecked
        {
            _state += 0x9E3779B97F4A7C15UL;
            ulong z = _state;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }
    }
}

// Reference-type long wrapper: forces a heap allocation per key/value, mirroring
// the JVM autoboxing a primitive long into a java.lang.Long object.
sealed class LongRef : IComparable<LongRef>
{
    public readonly long V;
    public LongRef(long v) => V = v;
    public int CompareTo(LongRef? other) => V.CompareTo(other!.V);
    public override bool Equals(object? o) => o is LongRef r && r.V == V;
    public override int GetHashCode() => V.GetHashCode();
}

static class Program
{
    // A "variant" is just the converter that maps a workload `long` onto the
    // dictionary's element type T. Both variants use key type == value type, and a
    // put reuses one wrapper object as both key and value — so ref-ref allocates one
    // heap object per put + one per get/remove, the same profile the harness has
    // always measured. Everything runs against the concrete
    // LockFreeSkipListDictionary<T,T> (no wrapper interface).
    static readonly Func<long, long> LongConv = k => k;
    static readonly Func<long, LongRef> RefConv = k => new LongRef(k);

    // =====================================================================
    //  Mixed 80/18/2 throughput
    // =====================================================================
    static long RunOnce<T>(Func<long, T> conv, int threadCount)
    {
        var map = new LockFreeSkipListDictionary<T, T>();

        // Deterministic pre-fill (single-threaded, not timed).
        var fillRng = new SplitMix64(0xDEADBEEFUL);
        ulong keyRange = (ulong)Workload.KeyRange;
        for (int i = 0; i < Workload.InitialKeys; i++)
        {
            var x = conv((long)(fillRng.Next() % keyRange));
            map[x] = x;
        }

        using var start = new ManualResetEventSlim(false);
        var threads = new Thread[threadCount];
        var ready = new CountdownEvent(threadCount);

        for (int t = 0; t < threadCount; t++)
        {
            int tid = t;
            threads[t] = new Thread(() =>
            {
                // Per-thread deterministic stream — same seeds as the Java harness.
                var rng = new SplitMix64(0x100UL + (ulong)tid);
                ready.Signal();
                start.Wait();
                long ops = Workload.OpsPerThread;
                for (long i = 0; i < ops; i++)
                {
                    ulong r = rng.Next();
                    long k = (long)((r >> 11) % keyRange);
                    int sel = (int)(r % 100);
                    if (sel < Workload.ReadPct) map.TryGetValue(conv(k), out _);
                    else if (sel < Workload.ReadPct + Workload.PutPct) { var x = conv(k); map[x] = x; }
                    else map.TryRemove(conv(k), out _);
                }
            }) { IsBackground = false, Name = $"bench-{t}" };
        }

        foreach (var th in threads) th.Start();
        ready.Wait();                      // all threads parked on the barrier
        var sw = Stopwatch.StartNew();
        start.Set();                       // release them together
        foreach (var th in threads) th.Join();
        sw.Stop();

        if (map.Count < 0) throw new Exception(); // keep work live
        return sw.ElapsedMilliseconds;
    }

    static (double best, double median) Measure<T>(Func<long, T> conv, int threadCount)
    {
        for (int w = 0; w < Workload.WarmupIters; w++) RunOnce(conv, threadCount);

        var totalOps = (double)Workload.OpsPerThread * threadCount;
        var samples = new List<double>();
        for (int m = 0; m < Workload.MeasureIters; m++)
        {
            long ms = RunOnce(conv, threadCount);
            samples.Add(totalOps / (ms / 1000.0) / 1e6);
        }
        samples.Sort();
        return (samples[^1], samples[samples.Count / 2]);
    }

    static void MixedVariant<T>(string name, Func<long, T> conv, bool csv, CultureInfo ci)
    {
        foreach (int tc in Workload.ThreadCounts)
        {
            if (tc > Environment.ProcessorCount) continue;
            var (best, median) = Measure(conv, tc);
            if (csv)
                Console.WriteLine(string.Format(ci, "{0},{1},{2:F2},{3:F2}", name, tc, best, median));
            else
                Console.WriteLine(string.Format(ci, "{0,-18} threads={1,-2}  median={2,7:F2} Mops/s   best={3,7:F2} Mops/s", name, tc, median, best));
        }
    }

    // =====================================================================
    //  Per-operation benchmark: each operation isolated, run on a dictionary
    //  prepared appropriately for it, so we measure that one operation path.
    // =====================================================================
    enum Op { GetHit, GetMiss, Update, Insert, Remove }

    static readonly (Op Op, string Name)[] OpList =
    {
        (Op.GetHit,  "get-hit"),
        (Op.GetMiss, "get-miss"),
        (Op.Update,  "update"),
        (Op.Insert,  "insert"),
        (Op.Remove,  "remove"),
    };

    static class OpCfg
    {
        public static long Prefill        = 500_000;    // for get/update
        public static long LookupsPerThread = 1_000_000; // for get/update
        public static long MutateTotal    = 2_000_000;  // total keys for insert/remove
        public static int  Warmup = 2, Measure = 3;

        public static void Quick()
        {
            Prefill = 50_000; LookupsPerThread = 200_000; MutateTotal = 200_000;
            Warmup = 1; Measure = 2;
        }
    }

    static long RunOp<T>(Func<long, T> conv, Op op, int threadCount)
    {
        var map = new LockFreeSkipListDictionary<T, T>();
        long prefill = OpCfg.Prefill;
        long mutateTotal = OpCfg.MutateTotal;
        long perThread = (op == Op.Insert || op == Op.Remove)
            ? mutateTotal / threadCount
            : OpCfg.LookupsPerThread;
        long actualMutate = perThread * threadCount;

        // ---- Prepare state (not timed) ----
        // get/update share an identical tree: the EVEN keys [0,2,..,2*prefill).
        // get-hit looks up evens (present); get-miss looks up odds (absent, scattered
        // between present nodes — a realistic miss, not a degenerate off-the-end one).
        switch (op)
        {
            case Op.GetHit:
            case Op.GetMiss:
            case Op.Update:
                for (long i = 0; i < prefill; i++) { var x = conv(2 * i); map[x] = x; }
                break;
            case Op.Remove:
                for (long i = 0; i < actualMutate; i++) { var x = conv(i); map[x] = x; }
                break;
            case Op.Insert:
                break; // start empty
        }

        using var start = new ManualResetEventSlim(false);
        var ready = new CountdownEvent(threadCount);
        var threads = new Thread[threadCount];
        for (int t = 0; t < threadCount; t++)
        {
            int tid = t;
            threads[t] = new Thread(() =>
            {
                var rng = new SplitMix64(0x5000UL + (ulong)tid);
                long lo = (long)tid * perThread;        // disjoint range for insert/remove
                ready.Signal();
                start.Wait();
                switch (op)
                {
                    case Op.GetHit:
                        for (long i = 0; i < perThread; i++)
                            map.TryGetValue(conv(2 * (long)(rng.Next() % (ulong)prefill)), out _);       // even -> present
                        break;
                    case Op.GetMiss:
                        for (long i = 0; i < perThread; i++)
                            map.TryGetValue(conv(2 * (long)(rng.Next() % (ulong)prefill) + 1), out _);   // odd -> absent
                        break;
                    case Op.Update:
                        for (long i = 0; i < perThread; i++)
                        {
                            var x = conv(2 * (long)(rng.Next() % (ulong)prefill));
                            map[x] = x;                                                                  // even -> overwrite
                        }
                        break;
                    case Op.Insert:
                        for (long i = 0; i < perThread; i++) { var x = conv(lo + i); map[x] = x; }
                        break;
                    case Op.Remove:
                        for (long i = 0; i < perThread; i++) map.TryRemove(conv(lo + i), out _);
                        break;
                }
            }) { IsBackground = false };
        }

        foreach (var th in threads) th.Start();
        ready.Wait();
        var sw = Stopwatch.StartNew();
        start.Set();
        foreach (var th in threads) th.Join();
        sw.Stop();
        if (map.Count < 0) throw new Exception();
        return sw.ElapsedMilliseconds;
    }

    static double MeasureOp<T>(Func<long, T> conv, Op op, int threadCount)
    {
        for (int w = 0; w < OpCfg.Warmup; w++) RunOp(conv, op, threadCount);
        long perThread = (op == Op.Insert || op == Op.Remove)
            ? OpCfg.MutateTotal / threadCount
            : OpCfg.LookupsPerThread;
        double totalOps = (double)perThread * threadCount;
        var samples = new List<double>();
        for (int m = 0; m < OpCfg.Measure; m++)
        {
            long ms = RunOp(conv, op, threadCount);
            samples.Add(totalOps / (Math.Max(1, ms) / 1000.0) / 1e6);
        }
        samples.Sort();
        return samples[samples.Count / 2]; // median
    }

    static void OpsVariant<T>(string name, Func<long, T> conv, bool csv, CultureInfo ci)
    {
        foreach (var (op, opName) in OpList)
        {
            foreach (int tc in Workload.ThreadCounts)
            {
                if (tc > Environment.ProcessorCount) continue;
                double median = MeasureOp(conv, op, tc);
                if (csv)
                    Console.WriteLine(string.Format(ci, "{0},{1},{2},{3:F2}", name, opName, tc, median));
                else
                    Console.WriteLine(string.Format(ci, "{0,-18} {1,-9} threads={2,-2}  median={3,7:F2} Mops/s", name, opName, tc, median));
            }
        }
    }

    // =====================================================================
    //  Reader/writer matrix: N dedicated writer threads churn the dictionary
    //  while M dedicated reader threads do lookups, concurrently. Duration-based.
    // =====================================================================
    sealed class Flag { public volatile bool Stop; }

    static class RwCfg
    {
        public static long Prefill = 500_000;     // even keys -> ~50% read hit rate
        public static int  DurationMs = 1000;
        public static int  Warmup = 1, Measure = 2;
        public static int[] Writers = { 1, 2, 4, 8 };
        public static int[] Readers = { 1, 2, 4, 8 };

        public static void Quick()
        {
            Prefill = 50_000; DurationMs = 300; Warmup = 1; Measure = 1;
            Writers = new[] { 1, 2, 4 }; Readers = new[] { 1, 2, 4 };
        }
    }

    static (double readMops, double writeMops) RunRw<T>(Func<long, T> conv, int writers, int readers)
    {
        var map = new LockFreeSkipListDictionary<T, T>();
        long prefill = RwCfg.Prefill;
        for (long i = 0; i < prefill; i++) { var x = conv(2 * i); map[x] = x; }   // even keys present
        ulong range = (ulong)(2 * prefill);

        var flag = new Flag();
        var ready = new CountdownEvent(writers + readers);
        using var start = new ManualResetEventSlim(false);
        var readCounts = new long[readers];
        var writeCounts = new long[writers];
        var threads = new List<Thread>(writers + readers);

        for (int r = 0; r < readers; r++)
        {
            int id = r;
            threads.Add(new Thread(() =>
            {
                var rng = new SplitMix64(0x9000UL + (ulong)id);
                ready.Signal(); start.Wait();
                long c = 0;
                while (!flag.Stop)
                {
                    for (int b = 0; b < 256; b++)                          // batch -> amortise the stop-flag read
                        map.TryGetValue(conv((long)(rng.Next() % range)), out _); // mix of hits (even) and misses (odd)
                    c += 256;
                }
                readCounts[id] = c;
            }) { IsBackground = false });
        }
        for (int w = 0; w < writers; w++)
        {
            int id = w;
            threads.Add(new Thread(() =>
            {
                var rng = new SplitMix64(0xA000UL + (ulong)id);
                ready.Signal(); start.Wait();
                long c = 0;
                while (!flag.Stop)
                {
                    for (int b = 0; b < 256; b++)
                    {
                        ulong r = rng.Next();
                        long k = (long)(r % range);
                        if ((r & 1) == 0) { var x = conv(k); map[x] = x; } else map.TryRemove(conv(k), out _); // 50/50 keeps size stable
                    }
                    c += 256;
                }
                writeCounts[id] = c;
            }) { IsBackground = false });
        }

        foreach (var t in threads) t.Start();
        ready.Wait();
        var sw = Stopwatch.StartNew();
        start.Set();
        Thread.Sleep(RwCfg.DurationMs);
        flag.Stop = true;
        foreach (var t in threads) t.Join();
        sw.Stop();

        double sec = sw.Elapsed.TotalSeconds;
        long reads = 0; foreach (var x in readCounts) reads += x;
        long writes = 0; foreach (var x in writeCounts) writes += x;
        return (reads / sec / 1e6, writes / sec / 1e6);
    }

    static (double readMops, double writeMops) MeasureRw<T>(Func<long, T> conv, int writers, int readers)
    {
        for (int w = 0; w < RwCfg.Warmup; w++) RunRw(conv, writers, readers);
        var rs = new List<double>(); var ws = new List<double>();
        for (int m = 0; m < RwCfg.Measure; m++)
        {
            var (r, wr) = RunRw(conv, writers, readers);
            rs.Add(r); ws.Add(wr);
        }
        rs.Sort(); ws.Sort();
        return (rs[rs.Count / 2], ws[ws.Count / 2]);
    }

    static void RwVariant<T>(string name, Func<long, T> conv, bool csv, CultureInfo ci)
    {
        foreach (int n in RwCfg.Writers)
        {
            foreach (int m in RwCfg.Readers)
            {
                var (rd, wr) = MeasureRw(conv, n, m);
                if (csv)
                    Console.WriteLine(string.Format(ci, "{0},{1},{2},{3:F2},{4:F2}", name, n, m, rd, wr));
                else
                    Console.WriteLine(string.Format(ci, "{0,-18} W={1,-2} R={2,-2}  read={3,7:F2}  write={4,7:F2} Mops/s", name, n, m, rd, wr));
            }
        }
    }

    static int Main(string[] args)
    {
        var ci = CultureInfo.InvariantCulture;
        bool csv = args.Contains("--csv");
        bool quick = args.Contains("--quick");
        if (quick) Workload.Quick();
        if (args.Contains("--small")) Workload.Small();
        string gc = System.Runtime.GCSettings.IsServerGC ? "Server" : "Workstation";

        if (args.Contains("--ops"))
        {
            if (quick) OpCfg.Quick();
            Console.WriteLine($"# .NET per-op benchmark  runtime={Environment.Version}  cores={Environment.ProcessorCount}  GC={gc}");
            Console.WriteLine($"# per-operation benchmark  prefill(get/update)={OpCfg.Prefill}  lookups/thread={OpCfg.LookupsPerThread}  insert/remove total={OpCfg.MutateTotal}");
            if (csv) Console.WriteLine("variant,operation,threads,median_mops");
            OpsVariant("dotnet-long-long", LongConv, csv, ci);
            OpsVariant("dotnet-ref-ref", RefConv, csv, ci);
            return 0;
        }

        if (args.Contains("--rw"))
        {
            if (quick) RwCfg.Quick();
            Console.WriteLine($"# .NET read/write-matrix benchmark  runtime={Environment.Version}  cores={Environment.ProcessorCount}  GC={gc}");
            Console.WriteLine($"# read/write matrix  prefill={RwCfg.Prefill} (even keys)  duration={RwCfg.DurationMs}ms  writers do 50% put / 50% remove, readers do get");
            if (csv) Console.WriteLine("variant,writers,readers,read_mops,write_mops");
            RwVariant("dotnet-long-long", LongConv, csv, ci);
            RwVariant("dotnet-ref-ref", RefConv, csv, ci);
            return 0;
        }

        if (args.Contains("--rwcompare"))
        {
            Console.WriteLine($"# readers×writers matrix — all .NET implementations  cores={Environment.ProcessorCount}  GC={gc}");
            RunRwCompare(quick);
            return 0;
        }

        if (args.Contains("--matrix"))
        {
            Console.WriteLine($"# crossover matrix  cores={Environment.ProcessorCount}  GC={gc}  (best Mops/s)");
            RunMatrix(quick);
            return 0;
        }

        if (args.Contains("--compare"))
        {
            Console.WriteLine($"# concurrent ordered-map comparison  cores={Environment.ProcessorCount}  GC={gc}");
            int pf = quick ? 100_000 : 1_000_000;
            long ot = quick ? 500_000 : 3_000_000;
            RunCompare(pf, ot, csv);
            return 0;
        }

        if (args.Contains("--mem"))
        {
            int memSize = quick ? 200_000 : 1_000_000;
            RunMem(memSize);
            return 0;
        }

        if (args.Contains("--memchurn"))
        {
            int memSize = quick ? 200_000 : 1_000_000;
            RunMemChurn(memSize);
            return 0;
        }

        if (args.Contains("--single"))
        {
            Console.WriteLine($"# single-threaded ordered-map comparison  runtime={Environment.Version}  GC={gc}");
            int[] sizes = quick ? new[] { 100_000 } : new[] { 100_000, 1_000_000 };
            foreach (int sz in sizes) RunSingleThreaded(sz, csv);
            return 0;
        }

        // Long single-threaded hot loop for CPU profiling. `--profile[:get|put|mixed]`.
        var prof = args.FirstOrDefault(a => a.StartsWith("--profile"));
        if (prof != null)
        {
            string which = prof.Contains(':') ? prof[(prof.IndexOf(':') + 1)..] : "mixed";
            RunProfileLoop(which, seconds: quick ? 5 : 25);
            return 0;
        }

        Console.WriteLine($"# .NET benchmark  runtime={Environment.Version}  cores={Environment.ProcessorCount}  GC={gc}");
        Console.WriteLine($"# workload: keyRange={Workload.KeyRange} initial={Workload.InitialKeys} ops/thread={Workload.OpsPerThread} mix(read/put/remove)={Workload.ReadPct}/{Workload.PutPct}/{100 - Workload.ReadPct - Workload.PutPct}");
        if (csv) Console.WriteLine("variant,threads,best_mops,median_mops");
        MixedVariant("dotnet-long-long", LongConv, csv, ci);
        MixedVariant("dotnet-ref-ref", RefConv, csv, ci);
        return 0;
    }

    // =====================================================================
    //  Single-threaded ordered-map comparison: B+-tree (several fanouts) vs the
    //  lock-free skip list vs SortedDictionary. Measures where cache locality shows
    //  up (large/scan) vs where it doesn't (small/cache-resident).
    // =====================================================================
    static double MedianMs(Action action, int warm, int iters)
    {
        for (int i = 0; i < warm; i++) action();
        var xs = new double[iters];
        for (int i = 0; i < iters; i++)
        {
            var sw = Stopwatch.StartNew();
            action();
            xs[i] = sw.Elapsed.TotalMilliseconds;
        }
        Array.Sort(xs);
        return xs[xs.Length / 2];
    }

    static long[] ShuffledKeys(int n, ulong seed)
    {
        var keys = new long[n];
        for (int i = 0; i < n; i++) keys[i] = i;
        var rng = new SplitMix64(seed);
        for (int i = n - 1; i > 0; i--) { int j = (int)(rng.Next() % (ulong)(i + 1)); (keys[i], keys[j]) = (keys[j], keys[i]); }
        return keys;
    }

    static void RunOne<T>(string name, int size, long lookups, long[] keys,
        Func<T> create, Action<T, long> insert, Func<T, long, bool> get, bool csv, CultureInfo ci)
        where T : IEnumerable<KeyValuePair<long, long>>
    {
        // BUILD from empty (median of measured runs)
        double buildMs = MedianMs(() => { var s = create(); foreach (var k in keys) insert(s, k); }, warm: 1, iters: 3);

        // prefilled instance for get / scan
        var inst = create();
        foreach (var k in keys) insert(inst, k);

        var hitRng = new SplitMix64(0x111);
        double hitMs = MedianMs(() => { for (long i = 0; i < lookups; i++) get(inst, (long)(hitRng.Next() % (ulong)size)); }, 1, 3);
        var missRng = new SplitMix64(0x222);
        double missMs = MedianMs(() => { for (long i = 0; i < lookups; i++) get(inst, size + (long)(missRng.Next() % (ulong)size)); }, 1, 3);

        long sink = 0;
        double scanMs = MedianMs(() => { sink = 0; foreach (var kv in inst) sink += kv.Key; }, 1, 3);
        if (sink == long.MinValue) Console.Write(' '); // keep scan live

        double Mops(double ms, double ops) => ops / (ms / 1000.0) / 1e6;
        double buildM = Mops(buildMs, size), hitM = Mops(hitMs, lookups), missM = Mops(missMs, lookups), scanM = Mops(scanMs, size);
        if (csv)
            Console.WriteLine(string.Format(ci, "{0},{1},{2:F2},{3:F2},{4:F2},{5:F2}", name, size, buildM, hitM, missM, scanM));
        else
            Console.WriteLine(string.Format(ci, "{0,-18} build={1,6:F2}  get-hit={2,6:F2}  get-miss={3,6:F2}  scan={4,7:F2}  Mops/s", name, buildM, hitM, missM, scanM));
    }

    // =====================================================================
    //  Concurrent 80/18/2 mixed comparison: concurrent B+-tree vs the skip list.
    //  Same SplitMix64 per-thread streams as the cross-language harness.
    // =====================================================================
    static double MeasureConcurrent<T>(Func<T> create, Action<T, long> put, Func<T, long, bool> get,
        Action<T, long> remove, int prefill, ulong range, long opsPerThread, int threadCount,
        int readPct = 80, int putPct = 18, int iters = 4)
    {
        double best = 0;
        for (int iter = 0; iter < iters; iter++)   // iter 0 = warmup, rest measured, keep best
        {
            var map = create();
            var fillRng = new SplitMix64(0xDEADBEEFUL);
            for (int i = 0; i < prefill; i++) put(map, (long)(fillRng.Next() % range));

            using var start = new ManualResetEventSlim(false);
            var ready = new CountdownEvent(threadCount);
            var threads = new Thread[threadCount];
            for (int t = 0; t < threadCount; t++)
            {
                int tid = t;
                threads[t] = new Thread(() =>
                {
                    var rng = new SplitMix64(0x100UL + (ulong)tid);
                    ready.Signal(); start.Wait();
                    for (long i = 0; i < opsPerThread; i++)
                    {
                        ulong r = rng.Next();
                        long k = (long)((r >> 11) % range);
                        int sel = (int)(r % 100);
                        if (sel < readPct) get(map, k);
                        else if (sel < readPct + putPct) put(map, k);
                        else remove(map, k);
                    }
                });
            }
            foreach (var th in threads) th.Start();
            ready.Wait();
            var sw = Stopwatch.StartNew();
            start.Set();
            foreach (var th in threads) th.Join();
            sw.Stop();
            if (iter == 0) continue;   // warmup
            double mops = (double)opsPerThread * threadCount / sw.Elapsed.TotalSeconds / 1e6;
            if (mops > best) best = mops;
        }
        return best;
    }

    // W dedicated writer threads (50% put / 50% remove) + R dedicated reader threads (get),
    // running concurrently for a fixed duration. Returns (read Mops/s, write Mops/s).
    static (double r, double w) MeasureRwGeneric<T>(Func<T> create, Action<T, long> put, Func<T, long, bool> get,
        Action<T, long> remove, int prefill, int writers, int readers, int durationMs, int measure)
    {
        double bestR = 0, bestW = 0;
        for (int iter = 0; iter <= measure; iter++)   // iter 0 = warmup
        {
            var map = create();
            for (long i = 0; i < prefill; i++) put(map, 2 * i);   // even keys
            ulong range = (ulong)(2 * prefill);

            var flag = new Flag();
            var ready = new CountdownEvent(writers + readers);
            using var start = new ManualResetEventSlim(false);
            var rc = new long[readers]; var wc = new long[writers];
            var ts = new List<Thread>(writers + readers);
            for (int r = 0; r < readers; r++)
            {
                int id = r;
                ts.Add(new Thread(() =>
                {
                    var rng = new SplitMix64(0x9000UL + (ulong)id);
                    ready.Signal(); start.Wait();
                    long c = 0;
                    while (!flag.Stop) { for (int b = 0; b < 256; b++) get(map, (long)(rng.Next() % range)); c += 256; }
                    rc[id] = c;
                }));
            }
            for (int w = 0; w < writers; w++)
            {
                int id = w;
                ts.Add(new Thread(() =>
                {
                    var rng = new SplitMix64(0xA000UL + (ulong)id);
                    ready.Signal(); start.Wait();
                    long c = 0;
                    while (!flag.Stop)
                    {
                        for (int b = 0; b < 256; b++) { ulong x = rng.Next(); long k = (long)(x % range); if ((x & 1) == 0) put(map, k); else remove(map, k); }
                        c += 256;
                    }
                    wc[id] = c;
                }));
            }
            foreach (var t in ts) t.Start();
            ready.Wait();
            var sw = Stopwatch.StartNew(); start.Set(); Thread.Sleep(durationMs); flag.Stop = true; foreach (var t in ts) t.Join(); sw.Stop();
            if (iter == 0) continue;
            double sec = sw.Elapsed.TotalSeconds;
            long reads = 0; foreach (var x in rc) reads += x;
            long writes = 0; foreach (var x in wc) writes += x;
            double rm = reads / sec / 1e6, wm = writes / sec / 1e6;
            if (rm + wm > bestR + bestW) { bestR = rm; bestW = wm; }
        }
        return (bestR, bestW);
    }

    static void RunRwCompare(bool quick)
    {
        var ci = CultureInfo.InvariantCulture;
        int prefill = quick ? 100_000 : 300_000;
        int dur = quick ? 300 : 600;
        int[] ws = { 1, 2, 4, 8 }, rs = { 1, 2, 4, 8 };
        Console.WriteLine($"# prefill={prefill:N0} (even keys)  duration={dur}ms  writers 50% put / 50% remove, readers get  (best Mops/s)");

        void Block<T>(string name, Func<T> create, Action<T, long> put, Func<T, long, bool> get, Action<T, long> rem)
        {
            var read = new double[ws.Length, rs.Length];
            var write = new double[ws.Length, rs.Length];
            for (int wi = 0; wi < ws.Length; wi++)
                for (int ri = 0; ri < rs.Length; ri++)
                {
                    if (ws[wi] + rs[ri] > 64) continue;
                    var (r, w) = MeasureRwGeneric(create, put, get, rem, prefill, ws[wi], rs[ri], dur, measure: 2);
                    read[wi, ri] = r; write[wi, ri] = w;
                }
            void PrintMatrix(string title, double[,] m)
            {
                Console.WriteLine($"  {name} — {title} (rows W ↓, cols R →)");
                Console.Write("    W\\R "); foreach (var r in rs) Console.Write(string.Format(ci, "{0,8}", "R=" + r)); Console.WriteLine();
                for (int wi = 0; wi < ws.Length; wi++)
                {
                    Console.Write(string.Format(ci, "    W={0,-3}", ws[wi]));
                    for (int ri = 0; ri < rs.Length; ri++) Console.Write(string.Format(ci, "{0,8:F2}", m[wi, ri]));
                    Console.WriteLine();
                }
            }
            Console.WriteLine($"\n## {name}");
            PrintMatrix("READ throughput", read);
            PrintMatrix("WRITE throughput", write);
        }

        Block("skiplist", () => new LockFreeSkipListDictionary<long, long>(),
            (d, k) => d[k] = k, (d, k) => d.TryGetValue(k, out _), (d, k) => d.TryRemove(k, out _));
        Block("bptree-64", () => new ConcurrentBPlusTree<long, long>(64),
            (d, k) => d[k] = k, (d, k) => d.TryGetValue(k, out _), (d, k) => d.TryRemove(k, out _));
        Block("bptree-256", () => new ConcurrentBPlusTree<long, long>(256),
            (d, k) => d[k] = k, (d, k) => d.TryGetValue(k, out _), (d, k) => d.TryRemove(k, out _));
        Block("blink-64", () => new BLinkTree<long, long>(64),
            (d, k) => d[k] = k, (d, k) => d.TryGetValue(k, out _), (d, k) => d.TryRemove(k, out _));
        Block("blink-256", () => new BLinkTree<long, long>(256),
            (d, k) => d[k] = k, (d, k) => d.TryGetValue(k, out _), (d, k) => d.TryRemove(k, out _));
        Block("csd-32", () => new ConcurrentSortedDictionary<long, long>(32),
            (d, k) => d[k] = k, (d, k) => d.TryGetValue(k, out _), (d, k) => d.TryRemove(k));
        Console.WriteLine();
    }

    static void RunMatrix(bool quick)
    {
        var ci = CultureInfo.InvariantCulture;
        int[] sizes = quick ? new[] { 100_000 } : new[] { 100_000, 1_000_000 };
        long ops = quick ? 400_000 : 1_500_000;
        int[] tcs = { 1, 2, 4, 8 };
        // (label, read%, put%) — remainder is remove%
        var mixes = new (string Name, int Read, int Put)[]
        {
            ("read-heavy 80/18/2", 80, 18),
            ("balanced  50/45/5",  50, 45),
            ("write-heavy 20/72/8", 20, 72),
        };

        foreach (int size in sizes)
        {
            ulong range = (ulong)size * 2;
            foreach (var mix in mixes)
            {
                Console.WriteLine($"\n## size={size:N0}  mix={mix.Name}");
                Console.WriteLine($"{"structure",-14} {"t=1",7} {"t=2",7} {"t=4",7} {"t=8",7}");

                void Row<T>(string name, Func<T> create, Action<T, long> put, Func<T, long, bool> get, Action<T, long> rem)
                {
                    Console.Write(string.Format(ci, "{0,-14}", name));
                    foreach (int tc in tcs)
                    {
                        if (tc > Environment.ProcessorCount) { Console.Write("       -"); continue; }
                        double m = MeasureConcurrent(create, put, get, rem, size, range, ops, tc, mix.Read, mix.Put, iters: 3);
                        Console.Write(string.Format(ci, " {0,7:F2}", m));
                    }
                    Console.WriteLine();
                }

                Row("skiplist", () => new LockFreeSkipListDictionary<long, long>(),
                    (d, k) => d[k] = k, (d, k) => d.TryGetValue(k, out _), (d, k) => d.TryRemove(k, out _));
                Row("bptree-64", () => new ConcurrentBPlusTree<long, long>(64),
                    (d, k) => d[k] = k, (d, k) => d.TryGetValue(k, out _), (d, k) => d.TryRemove(k, out _));
                Row("bptree-256", () => new ConcurrentBPlusTree<long, long>(256),
                    (d, k) => d[k] = k, (d, k) => d.TryGetValue(k, out _), (d, k) => d.TryRemove(k, out _));
                Row("csd-32", () => new ConcurrentSortedDictionary<long, long>(32),
                    (d, k) => d[k] = k, (d, k) => d.TryGetValue(k, out _), (d, k) => d.TryRemove(k));
            }
        }
        Console.WriteLine();
    }

    static void RunCompare(int prefill, long opsPerThread, bool csv)
    {
        ulong range = (ulong)prefill * 2;
        var ci = CultureInfo.InvariantCulture;
        Console.WriteLine($"# mixed 80/18/2  prefill={prefill:N0}  keyRange={range:N0}  ops/thread={opsPerThread:N0}  (best Mops/s)");
        int[] tcs = { 1, 2, 4, 8 };

        void Row<T>(string name, Func<T> create, Action<T, long> put, Func<T, long, bool> get, Action<T, long> remove)
        {
            Console.Write(string.Format(ci, "{0,-14}", name));
            foreach (int tc in tcs)
            {
                if (tc > Environment.ProcessorCount) { Console.Write("       -"); continue; }
                double m = MeasureConcurrent(create, put, get, remove, prefill, range, opsPerThread, tc);
                Console.Write(string.Format(ci, " {0,7:F2}", m));
            }
            Console.WriteLine();
        }

        Console.WriteLine($"{"structure",-14} {"t=1",7} {"t=2",7} {"t=4",7} {"t=8",7}");
        Row("skiplist", () => new LockFreeSkipListDictionary<long, long>(),
            (d, k) => d[k] = k, (d, k) => d.TryGetValue(k, out _), (d, k) => d.TryRemove(k, out _));
        foreach (int order in new[] { 64, 128, 256 })
            Row($"bptree-{order}", () => new ConcurrentBPlusTree<long, long>(order),
                (d, k) => d[k] = k, (d, k) => d.TryGetValue(k, out _), (d, k) => d.TryRemove(k, out _));
        foreach (int order in new[] { 64, 256 })
            Row($"blink-{order}", () => new BLinkTree<long, long>(order),
                (d, k) => d[k] = k, (d, k) => d.TryGetValue(k, out _), (d, k) => d.TryRemove(k, out _));
        foreach (int k in new[] { 32, 64 })
            Row($"csd-{k}", () => new ConcurrentSortedDictionary<long, long>(k),
                (d, key) => d[key] = key, (d, key) => d.TryGetValue(key, out _), (d, key) => d.TryRemove(key));
        Console.WriteLine();
    }

    static void MeasureMem<T>(string name, long[] keys, Func<T> create, Action<T, long> insert) where T : class
    {
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        long before = GC.GetTotalMemory(true);          // keys[] already live, counted in baseline
        T s = create();
        foreach (var k in keys) insert(s, k);
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        long after = GC.GetTotalMemory(true);
        GC.KeepAlive(s);
        long bytes = after - before;                    // structure only
        Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
            "{0,-18} {1,6:F1} bytes/entry   ({2,6:F1} MB)", name, bytes / (double)keys.Length, bytes / 1048576.0));
    }

    static void RunMem(int size)
    {
        var keys = ShuffledKeys(size, 0xBEEF);          // long[size] — live throughout, excluded from the delta
        Console.WriteLine($"# retained heap for {size:N0} <long,long> entries (measured via GC.GetTotalMemory(true))");
        MeasureMem("SortedDictionary", keys, () => new SortedDictionary<long, long>(), (d, k) => d[k] = k);
        MeasureMem("skiplist", keys, () => new LockFreeSkipListDictionary<long, long>(), (d, k) => d.TryAdd(k, k));
        foreach (int order in new[] { 64, 256 })
            MeasureMem($"bptree-st-{order}", keys, () => new BPlusTree<long, long>(order), (d, k) => d.TryAdd(k, k));
        foreach (int order in new[] { 64, 256 })
            MeasureMem($"bptree-conc-{order}", keys, () => new ConcurrentBPlusTree<long, long>(order), (d, k) => d.TryAdd(k, k));
        foreach (int order in new[] { 64, 256 })
            MeasureMem($"blink-{order}", keys, () => new BLinkTree<long, long>(order), (d, k) => d.TryAdd(k, k));
        foreach (int k in new[] { 32, 64 })
            MeasureMem($"csd-{k}", keys, () => new ConcurrentSortedDictionary<long, long>(k), (d, key) => d.TryAdd(key, key));
        Console.WriteLine();
    }

    // Retained heap for the LIVE set after building `size` entries then deleting all but `keep`.
    // This is where empty-leaf reclamation pays off vs. plain lazy delete: memory should track the
    // surviving keys, not the build-time peak. Compared against CSD (which merges -> bounded) and the
    // skip list (per-node, no bloat). bytes/entry is normalised to the SURVIVING entries.
    static void MeasureMemAfterDrain<T>(string name, long[] keys, bool[] keep, int keepCount,
        Func<T> create, Action<T, long> insert, Action<T, long> remove, Action<T>? finish = null) where T : class
    {
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        long before = GC.GetTotalMemory(true);
        T s = create();
        for (int i = 0; i < keys.Length; i++) insert(s, keys[i]);
        for (int i = 0; i < keys.Length; i++) if (!keep[i]) remove(s, keys[i]);   // drain to the survivors
        finish?.Invoke(s);                                                        // e.g. a compaction pass
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        long after = GC.GetTotalMemory(true);
        GC.KeepAlive(s);
        long bytes = after - before;
        if (bytes < 0)                                    // reclaimed below the baseline snapshot -> GC noise, report as ~0
            Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "{0,-18} {1,8} bytes/survivor   (~live-set size; GC baseline noise)", name, "~0"));
        else
            Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "{0,-18} {1,8:F1} bytes/survivor   ({2,6:F2} MB for {3:N0} live)", name, bytes / (double)keepCount, bytes / 1048576.0, keepCount));
    }

    static void RunMemChurn(int size)
    {
        var keys = ShuffledKeys(size, 0xBEEF);
        void Scenario(string title, bool[] keep, int keepCount)
        {
            Console.WriteLine($"\n## {title}: retained heap after draining {size:N0} -> {keepCount:N0} survivors");
            MeasureMemAfterDrain("skiplist", keys, keep, keepCount, () => new LockFreeSkipListDictionary<long, long>(),
                (d, k) => d.TryAdd(k, k), (d, k) => d.TryRemove(k, out _));
            foreach (int order in new[] { 64, 256 })
                MeasureMemAfterDrain($"bptree-conc-{order}", keys, keep, keepCount, () => new ConcurrentBPlusTree<long, long>(order),
                    (d, k) => d.TryAdd(k, k), (d, k) => d.TryRemove(k, out _));
            MeasureMemAfterDrain("blink-64 (lazy)", keys, keep, keepCount, () => new BLinkTree<long, long>(64),
                (d, k) => d.TryAdd(k, k), (d, k) => d.TryRemove(k, out _));
            MeasureMemAfterDrain("blink-64 (compacted)", keys, keep, keepCount, () => new BLinkTree<long, long>(64),
                (d, k) => d.TryAdd(k, k), (d, k) => d.TryRemove(k, out _), finish: d => d.Compact());
            foreach (int k in new[] { 32, 64 })
                MeasureMemAfterDrain($"csd-{k}", keys, keep, keepCount, () => new ConcurrentSortedDictionary<long, long>(k),
                    (d, key) => d.TryAdd(key, key), (d, key) => d.TryRemove(key));
        }

        // (a) SCATTERED: keep a random ~10%. Almost no leaf empties fully -> only merge reclaims the
        //     underfull bloat. Empty-leaf reclamation barely fires here.
        var keepRand = new bool[size]; int randCount = 0;
        var rng = new SplitMix64(0xD1CE);
        for (int i = 0; i < size; i++) if (rng.Next() % 10 == 0) { keepRand[i] = true; randCount++; }

        // (b) CONTIGUOUS: keep the bottom 10% of the key range, drain the top 90%. Whole leaves empty
        //     -> empty-leaf reclamation recovers the space (the range-delete / TTL pattern).
        var keepLow = new bool[size]; int lowCount = 0;
        long cutoff = size / 10;
        for (int i = 0; i < size; i++) if (keys[i] < cutoff) { keepLow[i] = true; lowCount++; }

        Console.WriteLine("# Does retained memory track the LIVE set or stay stuck at the build peak?");
        Scenario("(a) scattered random 10% kept", keepRand, randCount);
        Scenario("(b) contiguous bottom 10% kept (range drain)", keepLow, lowCount);
        Console.WriteLine();
    }

    static void RunSingleThreaded(int size, bool csv)
    {
        long lookups = 2_000_000;
        var keys = ShuffledKeys(size, 0xBEEF);
        var ci = CultureInfo.InvariantCulture;
        Console.WriteLine($"# size={size:N0}  lookups={lookups:N0}  (build = insert all from empty; scan = full ordered enumeration)");
        if (csv) Console.WriteLine("structure,size,build_mops,gethit_mops,getmiss_mops,scan_mops");

        RunOne("SortedDictionary", size, lookups, keys,
            () => new SortedDictionary<long, long>(),
            (d, k) => d[k] = k, (d, k) => d.ContainsKey(k), csv, ci);

        RunOne("skiplist", size, lookups, keys,
            () => new LockFreeSkipListDictionary<long, long>(),
            (d, k) => d.TryAdd(k, k), (d, k) => d.TryGetValue(k, out _), csv, ci);

        foreach (int order in new[] { 16, 32, 64, 128, 256 })
            RunOne($"bptree-{order}", size, lookups, keys,
                () => new BPlusTree<long, long>(order),
                (d, k) => d.TryAdd(k, k), (d, k) => d.TryGetValue(k, out _), csv, ci);

        foreach (int order in new[] { 64, 256 })
            RunOne($"blink-{order}", size, lookups, keys,
                () => new BLinkTree<long, long>(order),
                (d, k) => d.TryAdd(k, k), (d, k) => d.TryGetValue(k, out _), csv, ci);

        foreach (int k in new[] { 32, 64 })
            RunOne($"csd-{k}", size, lookups, keys,
                () => new ConcurrentSortedDictionary<long, long>(k),
                (d, key) => d.TryAdd(key, key), (d, key) => d.TryGetValue(key, out _), csv, ci);

        Console.WriteLine();
    }

    static void RunProfileLoop(string which, int seconds)
    {
        var map = new LockFreeSkipListDictionary<long, long>();
        var fill = new SplitMix64(0xDEADBEEFUL);
        ulong range = (ulong)Workload.KeyRange;
        for (int i = 0; i < Workload.InitialKeys; i++) { var x = (long)(fill.Next() % range); map[x] = x; }

        Console.WriteLine($"# profiling '{which}' single-thread on {Workload.InitialKeys} entries for ~{seconds}s …");
        var rng = new SplitMix64(0x100UL);
        var sw = Stopwatch.StartNew();
        long ops = 0;
        while (sw.Elapsed.TotalSeconds < seconds)
        {
            for (int b = 0; b < 200_000; b++)
            {
                ulong r = rng.Next();
                long k = (long)((r >> 11) % range);
                switch (which)
                {
                    case "get": map.TryGetValue(k, out _); break;
                    case "put": map[k] = k; break;
                    case "remove": map.TryRemove(k, out _); break;
                    default:    // mixed 80/18/2
                        int sel = (int)(r % 100);
                        if (sel < Workload.ReadPct) map.TryGetValue(k, out _);
                        else if (sel < Workload.ReadPct + Workload.PutPct) map[k] = k;
                        else map.TryRemove(k, out _);
                        break;
                }
                ops++;
            }
        }
        sw.Stop();
        Console.WriteLine($"# {ops:N0} ops in {sw.Elapsed.TotalSeconds:F1}s = {ops / sw.Elapsed.TotalSeconds / 1e6:F2} Mops/s  (count={map.Count})");
    }
}
