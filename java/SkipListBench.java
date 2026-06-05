import java.util.concurrent.ConcurrentSkipListMap;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.atomic.AtomicLong;

/**
 * Cross-language benchmark harness (Java side) — mirror image of the C# harness.
 *
 * Same SplitMix64 PRNG, same seeds, same key range, same operation mix, same
 * pre-fill, same thread counts, same warmup/measure iteration counts. Each thread
 * runs the identical operation stream as its C# counterpart.
 *
 * This benchmarks java.util.concurrent.ConcurrentSkipListMap<Long,Long>, which
 * autoboxes every primitive long key and value into a java.lang.Long object — the
 * direct analogue of the C# "ref-ref" variant.
 */
public class SkipListBench {

    // ---- workload constants: must match Workload in the C# Program.cs ----
    static int  KEY_RANGE      = 2_000_000;
    static int  INITIAL_KEYS   = 1_000_000;
    static long OPS_PER_THREAD = 4_000_000L;
    static final int  READ_PCT = 80;
    static final int  PUT_PCT  = 18;                // remove = 2%
    static int  WARMUP_ITERS   = 3;
    static int  MEASURE_ITERS  = 5;
    static final int[] THREAD_COUNTS = { 1, 2, 4, 8 };

    static void quick() {
        KEY_RANGE = 200_000; INITIAL_KEYS = 100_000; OPS_PER_THREAD = 500_000L;
        WARMUP_ITERS = 1; MEASURE_ITERS = 2;
    }

    // Cache-resident working set, full rigour (warmup/iterations unchanged).
    static void small() {
        KEY_RANGE = 200_000; INITIAL_KEYS = 100_000;
    }

    // SplitMix64 — bit-identical to the C# SplitMix64 struct.
    static final class SplitMix64 {
        private long state;
        SplitMix64(long seed) { this.state = seed; }
        long next() {
            state += 0x9E3779B97F4A7C15L;
            long z = state;
            z = (z ^ (z >>> 30)) * 0xBF58476D1CE4E5B9L;
            z = (z ^ (z >>> 27)) * 0x94D049BB133111EBL;
            return z ^ (z >>> 31);
        }
    }

    static long runOnce(int threadCount) throws InterruptedException {
        final ConcurrentSkipListMap<Long, Long> map = new ConcurrentSkipListMap<>();

        // Deterministic pre-fill (not timed). Matches C# pre-fill exactly.
        SplitMix64 fill = new SplitMix64(0xDEADBEEFL);
        for (int i = 0; i < INITIAL_KEYS; i++) {
            long k = Long.remainderUnsigned(fill.next(), KEY_RANGE);
            map.put(k, k);
        }

        final CountDownLatch ready = new CountDownLatch(threadCount);
        final CountDownLatch start = new CountDownLatch(1);
        final Thread[] threads = new Thread[threadCount];

        for (int t = 0; t < threadCount; t++) {
            final int tid = t;
            threads[t] = new Thread(() -> {
                SplitMix64 rng = new SplitMix64(0x100L + tid);   // same seed as C#
                ready.countDown();
                try { start.await(); } catch (InterruptedException e) { return; }
                long ops = OPS_PER_THREAD;
                for (long i = 0; i < ops; i++) {
                    long r = rng.next();
                    long key = Long.remainderUnsigned(r >>> 11, KEY_RANGE);
                    int sel = (int) Long.remainderUnsigned(r, 100);
                    if (sel < READ_PCT) {
                        map.get(key);                       // autobox key -> Long
                    } else if (sel < READ_PCT + PUT_PCT) {
                        map.put(key, key);                  // autobox key + value -> Long
                    } else {
                        map.remove(key);                    // autobox key -> Long
                    }
                }
            }, "bench-" + t);
        }

        for (Thread th : threads) th.start();
        ready.await();                       // all parked on the barrier
        long t0 = System.nanoTime();
        start.countDown();                   // release together
        for (Thread th : threads) th.join();
        long elapsedMs = (System.nanoTime() - t0) / 1_000_000L;

        if (map.size() < 0) throw new IllegalStateException(); // keep work live
        return elapsedMs;
    }

    static double[] measure(int threadCount) throws InterruptedException {
        for (int w = 0; w < WARMUP_ITERS; w++) runOnce(threadCount);

        double totalOps = (double) OPS_PER_THREAD * threadCount;
        double[] samples = new double[MEASURE_ITERS];
        for (int m = 0; m < MEASURE_ITERS; m++) {
            long ms = runOnce(threadCount);
            samples[m] = totalOps / (ms / 1000.0) / 1e6;
        }
        java.util.Arrays.sort(samples);
        double median = samples[samples.length / 2];
        double best = samples[samples.length - 1];
        return new double[] { best, median };
    }

    // =====================================================================
    //  Per-operation benchmark — mirror image of the C# --ops mode.
    // =====================================================================
    enum Op { GET_HIT, GET_MISS, UPDATE, INSERT, REMOVE }
    static final String[] OP_NAMES = { "get-hit", "get-miss", "update", "insert", "remove" };

    static long  OP_PREFILL  = 500_000;     // get/update tree size (EVEN keys)
    static long  OP_LOOKUPS  = 1_000_000;   // lookups per thread (get/update)
    static long  OP_MUTATE   = 2_000_000;   // total keys for insert/remove
    static int   OP_WARMUP = 2, OP_MEASURE = 3;

    static void opQuick() {
        OP_PREFILL = 50_000; OP_LOOKUPS = 200_000; OP_MUTATE = 200_000;
        OP_WARMUP = 1; OP_MEASURE = 2;
    }

    static long runOp(Op op, int threadCount) throws InterruptedException {
        final ConcurrentSkipListMap<Long, Long> map = new ConcurrentSkipListMap<>();
        final long prefill = OP_PREFILL;
        final long perThread = (op == Op.INSERT || op == Op.REMOVE)
                ? OP_MUTATE / threadCount : OP_LOOKUPS;
        final long actualMutate = perThread * threadCount;

        switch (op) {
            case GET_HIT: case GET_MISS: case UPDATE:
                for (long i = 0; i < prefill; i++) map.put(2 * i, 2 * i); // EVEN keys
                break;
            case REMOVE:
                for (long i = 0; i < actualMutate; i++) map.put(i, i);
                break;
            case INSERT:
                break; // empty
        }

        final CountDownLatch ready = new CountDownLatch(threadCount);
        final CountDownLatch start = new CountDownLatch(1);
        final Thread[] threads = new Thread[threadCount];
        for (int t = 0; t < threadCount; t++) {
            final int tid = t;
            threads[t] = new Thread(() -> {
                SplitMix64 rng = new SplitMix64(0x5000L + tid);
                long lo = (long) tid * perThread;
                ready.countDown();
                try { start.await(); } catch (InterruptedException e) { return; }
                switch (op) {
                    case GET_HIT:
                        for (long i = 0; i < perThread; i++)
                            map.get(2 * Long.remainderUnsigned(rng.next(), prefill));
                        break;
                    case GET_MISS:
                        for (long i = 0; i < perThread; i++)
                            map.get(2 * Long.remainderUnsigned(rng.next(), prefill) + 1);
                        break;
                    case UPDATE:
                        for (long i = 0; i < perThread; i++) {
                            long k = 2 * Long.remainderUnsigned(rng.next(), prefill);
                            map.put(k, k);
                        }
                        break;
                    case INSERT:
                        for (long i = 0; i < perThread; i++) { long k = lo + i; map.put(k, k); }
                        break;
                    case REMOVE:
                        for (long i = 0; i < perThread; i++) map.remove(lo + i);
                        break;
                }
            }, "op-" + t);
        }

        for (Thread th : threads) th.start();
        ready.await();
        long t0 = System.nanoTime();
        start.countDown();
        for (Thread th : threads) th.join();
        long ms = (System.nanoTime() - t0) / 1_000_000L;
        if (map.size() < 0) throw new IllegalStateException();
        return ms;
    }

    static double measureOp(Op op, int threadCount) throws InterruptedException {
        for (int w = 0; w < OP_WARMUP; w++) runOp(op, threadCount);
        long perThread = (op == Op.INSERT || op == Op.REMOVE)
                ? OP_MUTATE / threadCount : OP_LOOKUPS;
        double totalOps = (double) perThread * threadCount;
        double[] samples = new double[OP_MEASURE];
        for (int m = 0; m < OP_MEASURE; m++) {
            long ms = runOp(op, threadCount);
            samples[m] = totalOps / (Math.max(1, ms) / 1000.0) / 1e6;
        }
        java.util.Arrays.sort(samples);
        return samples[samples.length / 2];
    }

    static void runOpsMode(boolean csv) throws InterruptedException {
        int cores = Runtime.getRuntime().availableProcessors();
        System.out.printf("# Java per-op benchmark  jvm=%s  cores=%d  gc=%s%n",
                System.getProperty("java.version"), cores, System.getProperty("java.vm.name"));
        System.out.printf("# per-operation  prefill(get/update)=%d  lookups/thread=%d  insert/remove total=%d%n",
                OP_PREFILL, OP_LOOKUPS, OP_MUTATE);
        if (csv) System.out.println("variant,operation,threads,median_mops");
        Op[] ops = Op.values();
        for (int oi = 0; oi < ops.length; oi++) {
            for (int tc : THREAD_COUNTS) {
                if (tc > cores) continue;
                double median = measureOp(ops[oi], tc);
                if (csv)
                    System.out.printf("java-cslm-Long-Long,%s,%d,%.2f%n", OP_NAMES[oi], tc, median);
                else
                    System.out.printf("%-18s %-9s threads=%-2d  median=%7.2f Mops/s%n",
                            "java-cslm", OP_NAMES[oi], tc, median);
            }
        }
    }

    // =====================================================================
    //  Reader/writer matrix — mirror image of the C# --rw mode.
    // =====================================================================
    static long  RW_PREFILL  = 500_000;
    static int   RW_DURATION_MS = 1000;
    static int   RW_WARMUP = 1, RW_MEASURE = 2;
    static int[] RW_WRITERS = { 1, 2, 4, 8 };
    static int[] RW_READERS = { 1, 2, 4, 8 };

    static void rwQuick() {
        RW_PREFILL = 50_000; RW_DURATION_MS = 300; RW_WARMUP = 1; RW_MEASURE = 1;
        RW_WRITERS = new int[]{1, 2, 4}; RW_READERS = new int[]{1, 2, 4};
    }

    static double[] runRw(int writers, int readers) throws InterruptedException {
        final ConcurrentSkipListMap<Long, Long> map = new ConcurrentSkipListMap<>();
        final long prefill = RW_PREFILL;
        for (long i = 0; i < prefill; i++) map.put(2 * i, 2 * i);  // even keys present
        final long range = 2 * prefill;

        final java.util.concurrent.atomic.AtomicBoolean stop = new java.util.concurrent.atomic.AtomicBoolean(false);
        final CountDownLatch ready = new CountDownLatch(writers + readers);
        final CountDownLatch start = new CountDownLatch(1);
        final long[] readCounts = new long[readers];
        final long[] writeCounts = new long[writers];
        final Thread[] threads = new Thread[writers + readers];

        for (int r = 0; r < readers; r++) {
            final int id = r;
            threads[r] = new Thread(() -> {
                SplitMix64 rng = new SplitMix64(0x9000L + id);
                ready.countDown();
                try { start.await(); } catch (InterruptedException e) { return; }
                long c = 0;
                while (!stop.get()) {
                    for (int b = 0; b < 256; b++)
                        map.get(Long.remainderUnsigned(rng.next(), range));
                    c += 256;
                }
                readCounts[id] = c;
            }, "rd-" + r);
        }
        for (int w = 0; w < writers; w++) {
            final int id = w;
            threads[readers + w] = new Thread(() -> {
                SplitMix64 rng = new SplitMix64(0xA000L + id);
                ready.countDown();
                try { start.await(); } catch (InterruptedException e) { return; }
                long c = 0;
                while (!stop.get()) {
                    for (int b = 0; b < 256; b++) {
                        long x = rng.next();
                        long key = Long.remainderUnsigned(x, range);
                        if ((x & 1) == 0) map.put(key, key); else map.remove(key);
                    }
                    c += 256;
                }
                writeCounts[id] = c;
            }, "wr-" + w);
        }

        for (Thread t : threads) t.start();
        ready.await();
        long t0 = System.nanoTime();
        start.countDown();
        Thread.sleep(RW_DURATION_MS);
        stop.set(true);
        for (Thread t : threads) t.join();
        double sec = (System.nanoTime() - t0) / 1e9;

        long reads = 0; for (long x : readCounts) reads += x;
        long writes = 0; for (long x : writeCounts) writes += x;
        return new double[] { reads / sec / 1e6, writes / sec / 1e6 };
    }

    static double[] measureRw(int writers, int readers) throws InterruptedException {
        for (int w = 0; w < RW_WARMUP; w++) runRw(writers, readers);
        double[] rs = new double[RW_MEASURE], ws = new double[RW_MEASURE];
        for (int m = 0; m < RW_MEASURE; m++) {
            double[] r = runRw(writers, readers);
            rs[m] = r[0]; ws[m] = r[1];
        }
        java.util.Arrays.sort(rs); java.util.Arrays.sort(ws);
        return new double[] { rs[rs.length / 2], ws[ws.length / 2] };
    }

    static void runRwMode(boolean csv) throws InterruptedException {
        System.out.printf("# Java read/write-matrix  jvm=%s  cores=%d%n",
                System.getProperty("java.version"), Runtime.getRuntime().availableProcessors());
        System.out.printf("# read/write matrix  prefill=%d (even keys)  duration=%dms  writers 50%% put / 50%% remove, readers get%n",
                RW_PREFILL, RW_DURATION_MS);
        if (csv) System.out.println("variant,writers,readers,read_mops,write_mops");
        for (int n : RW_WRITERS) {
            for (int m : RW_READERS) {
                double[] r = measureRw(n, m);
                if (csv)
                    System.out.printf("java-cslm-Long-Long,%d,%d,%.2f,%.2f%n", n, m, r[0], r[1]);
                else
                    System.out.printf("%-18s W=%-2d R=%-2d  read=%7.2f  write=%7.2f Mops/s%n", "java-cslm", n, m, r[0], r[1]);
            }
        }
    }

    public static void main(String[] args) throws InterruptedException {
        boolean csv = false, ops = false, rw = false, quick = false;
        for (String a : args) {
            if (a.equals("--csv")) csv = true;
            else if (a.equals("--quick")) quick = true;
            else if (a.equals("--small")) small();
            else if (a.equals("--ops")) ops = true;
            else if (a.equals("--rw")) rw = true;
        }
        if (quick) { quick(); if (ops) opQuick(); if (rw) rwQuick(); }
        if (ops) { runOpsMode(csv); return; }
        if (rw) { runRwMode(csv); return; }
        int cores = Runtime.getRuntime().availableProcessors();
        System.out.printf("# Java benchmark  jvm=%s  cores=%d  gc=%s%n",
                System.getProperty("java.version"), cores,
                System.getProperty("java.vm.name"));
        System.out.printf("# workload: keyRange=%d initial=%d ops/thread=%d mix(read/put/remove)=%d/%d/%d%n",
                KEY_RANGE, INITIAL_KEYS, OPS_PER_THREAD, READ_PCT, PUT_PCT, 100 - READ_PCT - PUT_PCT);

        if (csv) System.out.println("variant,threads,best_mops,median_mops");
        for (int tc : THREAD_COUNTS) {
            if (tc > cores) continue;
            double[] r = measure(tc);
            if (csv)
                System.out.printf("java-cslm-Long-Long,%d,%.2f,%.2f%n", tc, r[0], r[1]);
            else
                System.out.printf("%-18s threads=%-2d  median=%7.2f Mops/s   best=%7.2f Mops/s%n",
                        "java-cslm", tc, r[1], r[0]);
        }
    }
}
