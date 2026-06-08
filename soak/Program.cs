using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Mobratil.Collections;

// =====================================================================
//  Sophisticated, long-running SOAK harness for the two shipped structures.
//  Cycles a battery of high-contention scenarios over varied collection sizes
//  and hot-spot intervals, each pairing heavy concurrent mutation with strong
//  validity checks (scan integrity, stable-set conservation, navigable
//  ordinality, structural Validate(), count reconciliation). Runs until
//  SOAK_SECONDS elapses; exits non-zero on the first invariant violation.
//
//  Env:
//    SOAK_SECONDS    total run time, seconds            (default 120)
//    SOAK_SLICE      seconds per scenario slice         (default 20)
//    SOAK_THREADS    writer threads (default 2x cores)
//    SOAK_SIZES      csv of collection sizes            (default 10,100,1000,10000,100000,1000000)
//                    tiny sizes (1-2 leaves) stress single-node latch contention + root growth/collapse
//    SOAK_ORDER      bptree node order                  (default 64)
//    SOAK_STRUCT     skiplist | bptree | both           (default both)
//  Value invariant everywhere: value == key, so any torn / cross-wired read is caught.
// =====================================================================

static class Soak
{
    static int Threads = Env("SOAK_THREADS", Math.Max(8, Environment.ProcessorCount * 2));
    static int Slice = Env("SOAK_SLICE", 20);
    static int Order = Env("SOAK_ORDER", 64);
    static long Fail0;                 // failure counter (atomic)
    static readonly object LogLock = new();

    static int Env(string k, int d) => int.TryParse(Environment.GetEnvironmentVariable(k), out var v) && v > 0 ? v : d;

    static void Log(string s) { lock (LogLock) Console.WriteLine($"[{DateTime.UtcNow:HH:mm:ss}] {s}"); }
    static void Fail(string s) { Interlocked.Increment(ref Fail0); lock (LogLock) Console.WriteLine($"[{DateTime.UtcNow:HH:mm:ss}] !!! FAIL: {s}"); }

    static int Main()
    {
        int totalSec = Env("SOAK_SECONDS", 120);
        var sizes = (Environment.GetEnvironmentVariable("SOAK_SIZES") ?? "10,100,1000,10000,100000,1000000")
                    .Split(',').Select(s => long.Parse(s.Trim())).ToArray();
        var structs = (Environment.GetEnvironmentVariable("SOAK_STRUCT") ?? "both") switch
        {
            "skiplist" => new[] { "skiplist" },
            "bptree" => new[] { "bptree" },
            _ => new[] { "skiplist", "bptree" }
        };
        var scenarios = new (string name, Action<Func<IMap>, long> run)[]
        {
            ("hot-0.1%",    (mk, n) => HotInterval(mk, n, 0.001)),
            ("hot-0.01%",   (mk, n) => HotInterval(mk, n, 0.0001)),
            ("band-scan",   (mk, n) => StableBandScan(mk, n)),
            ("boundary-hop",(mk, n) => BoundaryHop(mk, n)),
            ("disjoint",    (mk, n) => DisjointShardDrain(mk, n)),
            ("grow-shrink", (mk, n) => GrowShrink(mk, n)),
            ("navigable",   (mk, n) => NavigableOrdinal(mk, n)),
        };

        Log($"SOAK start: cores={Environment.ProcessorCount} threads={Threads} slice={Slice}s total={totalSec}s " +
            $"sizes=[{string.Join(",", sizes)}] order={Order} structs=[{string.Join(",", structs)}]");
        var sw = Stopwatch.StartNew();
        long slices = 0;
        while (sw.Elapsed.TotalSeconds < totalSec && Interlocked.Read(ref Fail0) == 0)
        {
            foreach (var st in structs)
                foreach (var n in sizes)
                    foreach (var (name, run) in scenarios)
                    {
                        if (sw.Elapsed.TotalSeconds >= totalSec || Interlocked.Read(ref Fail0) != 0) goto done;
                        Func<IMap> mk = st == "skiplist" ? () => new SkipMap() : () => new BTreeMap(Order);
                        var t0 = sw.Elapsed;
                        run(mk, n);
                        slices++;
                        Log($"ok  {st,-9} n={n,-8} {name,-12} ({(sw.Elapsed - t0).TotalSeconds:F0}s)  [{sw.Elapsed.TotalMinutes:F0}m/{totalSec / 60}m, {slices} slices]");
                    }
        }
    done:
        long fails = Interlocked.Read(ref Fail0);
        Log(fails == 0 ? $"ALL CLEAN — {slices} slices in {sw.Elapsed.TotalMinutes:F1}m" : $"{fails} FAILURE(S) in {slices} slices");
        return fails == 0 ? 0 : 1;
    }

    // ---- concurrency helpers -------------------------------------------------
    static List<Task> Spawn(int n, Action<int> body)
    {
        var t = new List<Task>(n);
        for (int i = 0; i < n; i++) { int id = i; t.Add(Task.Factory.StartNew(() => body(id), TaskCreationOptions.LongRunning)); }
        return t;
    }
    static void Prefill(IMap m, long n)
    {
        int p = Environment.ProcessorCount;
        Parallel.For(0, p, w => { for (long k = w; k < n; k += p) m[k] = k; });
    }

    // =====================================================================
    //  Scenario 1: HOT INTERVAL — massive parallel writes onto a tiny key band
    //  ([0, n*frac)) of an n-key map. Out-of-band keys are never touched, so every
    //  full scan must yield exactly (n - hotHi) of them — catches any key the
    //  split/merge storm on the hot leaves loses or duplicates. Quiescent check:
    //  Validate() + Count == enumerated + every out-of-band key intact.
    // =====================================================================
    static void HotInterval(Func<IMap> mk, long n, double frac)
    {
        var d = mk(); Prefill(d, n);
        long hotHi = Math.Max(2, (long)(n * frac));
        long stable = n - hotHi;
        using var stop = new CancellationTokenSource();

        var writers = Spawn(Threads, tid =>
        {
            var rng = new Random(tid * 2654435761u.GetHashCode() ^ tid);
            while (!stop.IsCancellationRequested)
            {
                long k = (long)(rng.NextDouble() * hotHi);
                if ((rng.Next() & 1) == 0) d[k] = k; else d.TryRemove(k, out _);
            }
        });
        // online checker: full scan must stay sorted+value-consistent and keep ALL stable keys
        long scans = 0; var r0 = d.Rebal();
        var checker = Spawn(2, _ =>
        {
            while (!stop.IsCancellationRequested)
            {
                long prev = long.MinValue, inStable = 0;
                foreach (var kv in d.All())
                {
                    if (kv.Key <= prev) { Fail($"hot {n} {frac}: scan order/dup {kv.Key} after {prev}"); return; }
                    if (kv.Value != kv.Key) { Fail($"hot {n} {frac}: torn {kv.Key}->{kv.Value}"); return; }
                    prev = kv.Key;
                    if (kv.Key >= hotHi) inStable++;
                }
                if (inStable != stable) { Fail($"hot {n} {frac}: stable lost/dup saw {inStable} expected {stable}"); return; }
                Interlocked.Increment(ref scans);
            }
        });
        Thread.Sleep(Slice * 1000);
        stop.Cancel(); Task.WaitAll(writers.Concat(checker).ToArray());

        var r1 = d.Rebal();
        Log($"     hot n={n} frac={frac}: splits={r1.splits - r0.splits} merges={r1.merges - r0.merges} full-scans={scans} (during the slice)");
        // quiescent reconcile
        Quiesce(d, $"hot {n} {frac}", expectStableLo: hotHi, expectStableHi: n);
    }

    // =====================================================================
    //  Scenario 2: STABLE BAND under churn, observed through a RANGE VIEW.
    //  A middle band is never mutated; everything else churns. A view spanning the
    //  band must always yield exactly the band, in order, value-consistent.
    // =====================================================================
    static void StableBandScan(Func<IMap> mk, long n)
    {
        var d = mk(); Prefill(d, n);
        long sLo = n / 2, sHi = sLo + Math.Min(2000, n / 50);      // stable band
        long vLo = sLo - n / 20, vHi = sHi + n / 20;               // view around it
        long band = sHi - sLo;
        using var stop = new CancellationTokenSource();
        var writers = Spawn(Threads, tid =>
        {
            var rng = new Random(tid * 40503 + 7);
            while (!stop.IsCancellationRequested)
            {
                long k = (long)(rng.NextDouble() * n);
                if (k >= sLo && k < sHi) continue;                 // leave the band pristine
                if ((rng.Next() & 1) == 0) d[k] = k; else d.TryRemove(k, out _);
            }
        });
        var checker = Spawn(1, _ =>
        {
            while (!stop.IsCancellationRequested)
            {
                long prev = long.MinValue, inBand = 0;
                foreach (var kv in d.ViewBetween(vLo, vHi))
                {
                    if (kv.Key < vLo || kv.Key >= vHi) { Fail($"band {n}: view out of range {kv.Key}"); return; }
                    if (kv.Key <= prev) { Fail($"band {n}: view order/dup {kv.Key} after {prev}"); return; }
                    if (kv.Value != kv.Key) { Fail($"band {n}: torn {kv.Key}->{kv.Value}"); return; }
                    prev = kv.Key;
                    if (kv.Key >= sLo && kv.Key < sHi) inBand++;
                }
                if (inBand != band) { Fail($"band {n}: band lost/dup saw {inBand} expected {band}"); return; }
            }
        });
        Thread.Sleep(Slice * 1000);
        stop.Cancel(); Task.WaitAll(writers.Concat(checker).ToArray());
        d.Validate();
    }

    // =====================================================================
    //  Scenario 2b: BOUNDARY HOP — the sharpest leaf-transition test. SPARSE anchor keys
    //  (about one per leaf, never removed) are surrounded by non-anchor keys that are
    //  OSCILLATED fill<->drain, which forces an anchor's leaf to split while filling and
    //  merge while draining, continuously. A scanner hops across those actively
    //  rebalancing boundaries while asserting every anchor still appears exactly once, so
    //  if a leaf-to-leaf hop ever dropped a key mid split/merge, the anchor count breaks.
    //  (The earlier dense-EVENS design was vacuous — pinned 50%-dense anchors held every
    //   leaf strictly between order/3 and order, so it measured ZERO splits/merges.)
    // =====================================================================
    static void BoundaryHop(Func<IMap> mk, long n)
    {
        var d = mk();
        const long G = 64;                                 // anchor spacing (~1 anchor per leaf)
        long A = Math.Min(4000, Math.Max(16, n / 1000));   // # sparse anchors
        long span = A * G;                                 // key range [0, span)
        for (long i = 0; i < A; i++) d[i * G] = i * G;     // sparse anchors — never removed
        using var stop = new CancellationTokenSource();
        long scans = 0; var r0 = d.Rebal();
        // self-test: with SOAK_INJECT_GAP=1, delete one never-removed anchor mid-slice — the gap
        // check above MUST then fail, naming that exact key. Proves the conservation check isn't vacuous.
        if (Environment.GetEnvironmentVariable("SOAK_INJECT_GAP") == "1")
            Task.Run(() => { Thread.Sleep(800); d.TryRemove((A / 2) * G, out _); });
        // each thread owns a disjoint shard of the NON-anchor keys and oscillates it
        // fill -> drain, driving its leaves across the split/merge thresholds repeatedly.
        var writers = Spawn(Threads, tid =>
        {
            while (!stop.IsCancellationRequested)
            {
                for (long k = tid; k < span && !stop.IsCancellationRequested; k += Threads)
                    if (k % G != 0) d[k] = k;                          // FILL  -> splits
                for (long k = tid; k < span && !stop.IsCancellationRequested; k += Threads)
                    if (k % G != 0) d.TryRemove(k, out _);             // DRAIN -> merges
            }
        });
        var checker = Spawn(2, _ =>
        {
            var seen = new bool[(int)A];          // reusable presence map over the never-removed anchors
            while (!stop.IsCancellationRequested)
            {
                Array.Clear(seen);
                long prev = long.MinValue;
                foreach (var kv in d.All())
                {
                    if (kv.Key <= prev) { Fail($"boundary {n}: scan order/dup {kv.Key} after {prev}"); return; }
                    if (kv.Value != kv.Key) { Fail($"boundary {n}: torn {kv.Key}->{kv.Value}"); return; }
                    prev = kv.Key;
                    if (kv.Key % G == 0) seen[(int)(kv.Key / G)] = true;   // mark this never-removed anchor present
                }
                // explicit GAP check: EVERY never-removed key must have appeared — report the exact one if not.
                for (int i = 0; i < A; i++)
                    if (!seen[i]) { Fail($"boundary {n}: MISSING never-removed key {(long)i * G} (value should be {(long)i * G})"); return; }
                Interlocked.Increment(ref scans);
            }
        });
        Thread.Sleep(Slice * 1000);
        stop.Cancel(); Task.WaitAll(writers.Concat(checker).ToArray());
        var r1 = d.Rebal();
        Log($"     boundary n={n}: splits={r1.splits - r0.splits} merges={r1.merges - r0.merges} full-scans={scans} (during the slice)");
        d.Validate();
        long aSeen = 0, prevq = long.MinValue;
        foreach (var kv in d.All())
        {
            if (kv.Key <= prevq) { Fail($"boundary {n}: quiescent order/dup {kv.Key}"); return; }
            if (kv.Value != kv.Key) { Fail($"boundary {n}: quiescent torn {kv.Key}->{kv.Value}"); return; }
            prevq = kv.Key;
            if (kv.Key % G == 0) aSeen++;
        }
        if (aSeen != A) Fail($"boundary {n}: quiescent anchors {aSeen} != {A}");
    }

    // =====================================================================
    //  Scenario 3: DISJOINT shards churned in parallel (no logical conflict), then a
    //  deterministic "fill all" barrier: every thread re-inserts its whole shard, so
    //  the final state must be exactly [0,n) — caught via Validate + exact count + scan.
    // =====================================================================
    static void DisjointShardDrain(Func<IMap> mk, long n)
    {
        var d = mk(); Prefill(d, n);
        using var stop = new CancellationTokenSource();
        var writers = Spawn(Threads, tid =>
        {
            var rng = new Random(tid * 19349663 + 1);
            while (!stop.IsCancellationRequested)
            {
                long k = tid + (long)(rng.NextDouble() * (n / Threads)) * Threads;   // this thread's shard
                if (k >= n) continue;
                if ((rng.Next() & 1) == 0) d[k] = k; else d.TryRemove(k, out _);
            }
        });
        Thread.Sleep(Slice * 1000);
        stop.Cancel(); Task.WaitAll(writers.ToArray());

        // deterministic checkpoint: every thread re-inserts its shard -> full set
        Task.WaitAll(Spawn(Threads, tid => { for (long k = tid; k < n; k += Threads) d[k] = k; }).ToArray());
        d.Validate();
        long prev = long.MinValue, c = 0;
        foreach (var kv in d.All())
        {
            if (kv.Key != prev + 1 && prev != long.MinValue) { Fail($"disjoint {n}: gap/dup at {kv.Key} after {prev}"); return; }
            if (kv.Value != kv.Key) { Fail($"disjoint {n}: torn {kv.Key}->{kv.Value}"); return; }
            prev = kv.Key; c++;
        }
        if (c != n) Fail($"disjoint {n}: count {c} != {n} after fill-all");
    }

    // =====================================================================
    //  Scenario 4: GROW / SHRINK — concurrent fill to full then drain to empty,
    //  repeatedly, with structural checks at both ends (root growth & collapse).
    // =====================================================================
    static void GrowShrink(Func<IMap> mk, long n)
    {
        long m = Math.Min(n, 200_000);     // keep cycles snappy
        var d = mk();
        var endAt = DateTime.UtcNow.AddSeconds(Slice);
        while (DateTime.UtcNow < endAt && Interlocked.Read(ref Fail0) == 0)
        {
            Task.WaitAll(Spawn(Threads, tid => { for (long k = tid; k < m; k += Threads) d[k] = k; }).ToArray());
            d.Validate();
            if (d.Count != m) { Fail($"grow {n}: filled count {d.Count} != {m}"); return; }
            Task.WaitAll(Spawn(Threads, tid => { for (long k = tid; k < m; k += Threads) d.TryRemove(k, out _); }).ToArray());
            d.Validate();
            if (d.Count != 0) { Fail($"shrink {n}: drained count {d.Count} != 0"); return; }
        }
    }

    // =====================================================================
    //  Scenario 5: NAVIGABLE ordinality under churn — ceiling>=k, floor<=k,
    //  higher>k, lower<k must hold for every returned entry, always.
    // =====================================================================
    static void NavigableOrdinal(Func<IMap> mk, long n)
    {
        var d = mk(); Prefill(d, n);
        using var stop = new CancellationTokenSource();
        var writers = Spawn(Threads, tid =>
        {
            var rng = new Random(tid * 83492791 + 3);
            while (!stop.IsCancellationRequested)
            {
                long k = (long)(rng.NextDouble() * n);
                if ((rng.Next() & 1) == 0) d[k] = k; else d.TryRemove(k, out _);
            }
        });
        var checker = Spawn(2, _ =>
        {
            var rng = new Random(12345);
            while (!stop.IsCancellationRequested)
            {
                long p = (long)(rng.NextDouble() * n);
                if (d.TryGetCeiling(p, out var c) && c.Key < p) { Fail($"nav {n}: ceiling {c.Key} < {p}"); return; }
                if (d.TryGetFloor(p, out var f) && f.Key > p) { Fail($"nav {n}: floor {f.Key} > {p}"); return; }
                if (d.TryGetHigher(p, out var h) && h.Key <= p) { Fail($"nav {n}: higher {h.Key} <= {p}"); return; }
                if (d.TryGetLower(p, out var l) && l.Key >= p) { Fail($"nav {n}: lower {l.Key} >= {p}"); return; }
            }
        });
        Thread.Sleep(Slice * 1000);
        stop.Cancel(); Task.WaitAll(writers.Concat(checker).ToArray());
        d.Validate();
    }

    // quiescent reconcile shared by hot scenario: structure valid, sorted, value==key,
    // count matches enumeration, and the stable interval [lo,hi) is fully intact.
    static void Quiesce(IMap d, string ctx, long expectStableLo, long expectStableHi)
    {
        d.Validate();
        long prev = long.MinValue, c = 0, stableSeen = 0;
        foreach (var kv in d.All())
        {
            if (kv.Key <= prev) { Fail($"{ctx}: quiescent order/dup {kv.Key} after {prev}"); return; }
            if (kv.Value != kv.Key) { Fail($"{ctx}: quiescent torn {kv.Key}->{kv.Value}"); return; }
            prev = kv.Key; c++;
            if (kv.Key >= expectStableLo && kv.Key < expectStableHi) stableSeen++;
        }
        if (c != d.Count) Fail($"{ctx}: quiescent count {d.Count} != enumerated {c}");
        if (stableSeen != expectStableHi - expectStableLo) Fail($"{ctx}: quiescent stable {stableSeen} != {expectStableHi - expectStableLo}");
    }
}

// ---- uniform interface over the two structures ----
interface IMap
{
    long this[long k] { set; }
    bool TryAdd(long k, long v);
    bool TryRemove(long k, out long v);
    bool TryGetValue(long k, out long v);
    bool TryGetCeiling(long k, out KeyValuePair<long, long> e);
    bool TryGetFloor(long k, out KeyValuePair<long, long> e);
    bool TryGetHigher(long k, out KeyValuePair<long, long> e);
    bool TryGetLower(long k, out KeyValuePair<long, long> e);
    int Count { get; }
    IEnumerable<KeyValuePair<long, long>> All();
    IEnumerable<KeyValuePair<long, long>> ViewBetween(long lo, long hi);
    void Validate();
    (long splits, long merges) Rebal();
}

sealed class SkipMap : IMap
{
    readonly ConcurrentSkipListDictionary<long, long> m = new();
    public long this[long k] { set => m[k] = value; }
    public bool TryAdd(long k, long v) => m.TryAdd(k, v);
    public bool TryRemove(long k, out long v) => m.TryRemove(k, out v);
    public bool TryGetValue(long k, out long v) => m.TryGetValue(k, out v);
    public bool TryGetCeiling(long k, out KeyValuePair<long, long> e) => m.TryGetCeiling(k, out e);
    public bool TryGetFloor(long k, out KeyValuePair<long, long> e) => m.TryGetFloor(k, out e);
    public bool TryGetHigher(long k, out KeyValuePair<long, long> e) => m.TryGetHigher(k, out e);
    public bool TryGetLower(long k, out KeyValuePair<long, long> e) => m.TryGetLower(k, out e);
    public int Count => m.Count;
    public IEnumerable<KeyValuePair<long, long>> All() => m;
    public IEnumerable<KeyValuePair<long, long>> ViewBetween(long lo, long hi) => m.GetViewBetween(lo, hi);
    public void Validate() { /* skip list has no structural validator */ }
    public (long splits, long merges) Rebal() => (0, 0);
}

sealed class BTreeMap : IMap
{
    readonly ConcurrentBTreeDictionary<long, long> m;
    public BTreeMap(int order) => m = new ConcurrentBTreeDictionary<long, long>(order);
    public long this[long k] { set => m[k] = value; }
    public bool TryAdd(long k, long v) => m.TryAdd(k, v);
    public bool TryRemove(long k, out long v) => m.TryRemove(k, out v);
    public bool TryGetValue(long k, out long v) => m.TryGetValue(k, out v);
    public bool TryGetCeiling(long k, out KeyValuePair<long, long> e) => m.TryGetCeiling(k, out e);
    public bool TryGetFloor(long k, out KeyValuePair<long, long> e) => m.TryGetFloor(k, out e);
    public bool TryGetHigher(long k, out KeyValuePair<long, long> e) => m.TryGetHigher(k, out e);
    public bool TryGetLower(long k, out KeyValuePair<long, long> e) => m.TryGetLower(k, out e);
    public int Count => m.Count;
    public IEnumerable<KeyValuePair<long, long>> All() => m;
    public IEnumerable<KeyValuePair<long, long>> ViewBetween(long lo, long hi) => m.GetViewBetween(lo, hi);
    public void Validate() => m.Validate();
    public (long splits, long merges) Rebal() => m.RebalanceCounts;
}
