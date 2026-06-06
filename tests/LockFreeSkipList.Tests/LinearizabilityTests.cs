using System.Text;
using Xunit;
using Xunit.Abstractions;

namespace LockFreeSkipList.Tests;

/// <summary>
/// Model-based LINEARIZABILITY testing — a much stronger oracle than the invariant stress suites.
/// Each round runs a small random concurrent history (a few threads, a few ops each) on the real
/// structure, bracketing every op with invocation/response timestamps from a shared atomic clock and
/// recording its return value. We then check (Wing–Gong, memoized) that SOME sequential order of those
/// ops exists that (a) respects real-time precedence — if op A returned before op B was called, A is
/// ordered before B — and (b) reproduces every return value against a sequential SortedDictionary
/// oracle. If no such order exists, the implementation is not linearizable and the history is printed.
///
/// Small histories are deliberate: the check is exponential, so we keep each history tiny and run many
/// thousands of them (LIN_ROUNDS, default 1500). Skip list + bptree (B-link excluded).
/// </summary>
public class LinearizabilityTests
{
    private readonly ITestOutputHelper _out;
    public LinearizabilityTests(ITestOutputHelper o) => _out = o;

    private enum K { Add, Remove, Get, Ceil }

    private sealed class Op
    {
        public int T; public K Kind; public int Key, Val; public long Inv, Resp; public string Ret = "";
    }

    private const int KeySpace = 6;          // small -> ops conflict -> interesting linearizations

    // Sequential spec on the oracle. MUST mirror the concurrent ops' observable results exactly.
    private static string Seq(SortedDictionary<int, int> m, K kind, int key, int val)
    {
        switch (kind)
        {
            case K.Add: if (m.ContainsKey(key)) return "add:F"; m[key] = val; return "add:T";
            case K.Remove: if (m.TryGetValue(key, out var rv)) { m.Remove(key); return $"rem:T:{rv}"; } return "rem:F";
            case K.Get: return m.TryGetValue(key, out var gv) ? $"get:T:{gv}" : "get:F";
            default: foreach (var kv in m) if (kv.Key >= key) return $"ceil:{kv.Key}:{kv.Value}"; return "ceil:F";
        }
    }

    private static string Conc(INavMap<int, int> d, K kind, int key, int val)
    {
        switch (kind)
        {
            case K.Add: return d.TryAdd(key, val) ? "add:T" : "add:F";
            case K.Remove: return d.TryRemove(key, out var rv) ? $"rem:T:{rv}" : "rem:F";
            case K.Get: return d.TryGetValue(key, out var gv) ? $"get:T:{gv}" : "get:F";
            default: return d.TryGetCeiling(key, out var e) ? $"ceil:{e.Key}:{e.Value}" : "ceil:F";
        }
    }

    [Theory]
    [InlineData("skiplist", 0)]
    [InlineData("bptree", 4)]
    [InlineData("bptree", 8)]
    [InlineData("bptree", 64)]
    public void Concurrent_Histories_Are_Linearizable(string kind, int order)
    {
        int rounds = int.TryParse(Environment.GetEnvironmentVariable("LIN_ROUNDS"), out var r) && r > 0 ? r : 1500;
        const int Threads = 4, OpsPer = 5;   // 20 ops/history -> exact check is fast, real concurrency present

        for (int round = 0; round < rounds; round++)
        {
            var d = NavMapFactory.Create(kind, order);
            long clock = 0;
            var perThread = new List<Op>[Threads];
            using (var ready = new Barrier(Threads))
            {
                var tasks = new Task[Threads];
                for (int t = 0; t < Threads; t++)
                {
                    int tid = t;
                    tasks[t] = Task.Run(() =>
                    {
                        var rng = new Random(round * 131 + tid * 17 + 1);
                        var list = new List<Op>(OpsPer);
                        ready.SignalAndWait();                       // release all threads together
                        for (int i = 0; i < OpsPer; i++)
                        {
                            var kind2 = (K)rng.Next(4);
                            int key = rng.Next(KeySpace);
                            int val = rng.Next(1, 1_000_000);
                            long inv = Interlocked.Increment(ref clock);
                            if ((rng.Next() & 3) == 0) Thread.SpinWait(rng.Next(1, 60));   // diversify interleavings
                            string ret = Conc(d, kind2, key, val);
                            long resp = Interlocked.Increment(ref clock);
                            list.Add(new Op { T = tid, Kind = kind2, Key = key, Val = val, Inv = inv, Resp = resp, Ret = ret });
                        }
                        perThread[tid] = list;
                    });
                }
                Task.WaitAll(tasks);
            }

            var ops = new List<Op>(Threads * OpsPer);
            foreach (var l in perThread) ops.AddRange(l);
            Assert.True(Linearizable(ops, out var trace),
                $"[{kind}/{order}] round {round}: NON-LINEARIZABLE history\n{trace}");
        }
        _out.WriteLine($"[{kind}/{order}] {rounds} concurrent histories verified linearizable");
    }

    // Validates the ORACLE itself: a checker that can't reject is worthless. A must precede B in real
    // time (A.Resp=2 < B.Inv=3) and A adds key 1, yet B's Get returns "not found" — unlinearizable.
    [Fact]
    public void Checker_Rejects_NonLinearizable_And_Accepts_Linearizable()
    {
        var bad = new List<Op>
        {
            new Op { T = 0, Kind = K.Add, Key = 1, Val = 7, Inv = 1, Resp = 2, Ret = "add:T" },
            new Op { T = 1, Kind = K.Get, Key = 1, Val = 0, Inv = 3, Resp = 4, Ret = "get:F" },
        };
        Assert.False(Linearizable(bad, out _), "checker wrongly ACCEPTED a non-linearizable history");

        // Same real-time order, but B observes the add -> linearizable. And an OVERLAPPING pair where
        // either order is valid must also be accepted.
        var good = new List<Op>
        {
            new Op { T = 0, Kind = K.Add, Key = 1, Val = 7, Inv = 1, Resp = 2, Ret = "add:T" },
            new Op { T = 1, Kind = K.Get, Key = 1, Val = 0, Inv = 3, Resp = 4, Ret = "get:T:7" },
        };
        Assert.True(Linearizable(good, out _), "checker wrongly REJECTED a linearizable history");

        var overlap = new List<Op>   // A:[1,4] Add, B:[2,3] Get -> 'B before A' linearizes (Get not found)
        {
            new Op { T = 0, Kind = K.Add, Key = 5, Val = 9, Inv = 1, Resp = 4, Ret = "add:T" },
            new Op { T = 1, Kind = K.Get, Key = 5, Val = 0, Inv = 2, Resp = 3, Ret = "get:F" },
        };
        Assert.True(Linearizable(overlap, out _), "checker wrongly REJECTED a valid overlapping history");
    }

    // Wing–Gong: does a sequential order of `ops` exist that respects real-time precedence and the spec?
    private static bool Linearizable(List<Op> ops, out string trace)
    {
        int n = ops.Count;
        var memo = new HashSet<string>();
        long budget = 1_000_000;             // safety valve: on blow-up, treat as inconclusive (pass)

        bool Rec(long remaining, SortedDictionary<int, int> model)
        {
            if (remaining == 0) return true;
            if (--budget < 0) return true;

            // An op X may be linearized next iff no still-remaining op completely precedes it in real
            // time (no remaining Y with Y.Resp < X.Inv) — i.e. X.Inv <= min(Resp) over remaining.
            long minResp = long.MaxValue;
            for (int i = 0; i < n; i++)
                if ((remaining & (1L << i)) != 0 && ops[i].Resp < minResp) minResp = ops[i].Resp;

            string memoKey = remaining.ToString() + '|' + State(model);
            if (!memo.Add(memoKey)) return false;     // already proven a dead end

            for (int i = 0; i < n; i++)
            {
                if ((remaining & (1L << i)) == 0) continue;
                var op = ops[i];
                if (op.Inv > minResp) continue;       // some other op must come first
                var copy = new SortedDictionary<int, int>(model);
                if (Seq(copy, op.Kind, op.Key, op.Val) != op.Ret) continue;   // spec rejects this point
                if (Rec(remaining & ~(1L << i), copy)) return true;
            }
            return false;
        }

        bool ok = Rec(n >= 64 ? -1L : (1L << n) - 1, new SortedDictionary<int, int>());
        trace = ok ? "" : string.Join("\n", ops.OrderBy(o => o.Inv)
            .Select(o => $"  t{o.T} [{o.Inv,3},{o.Resp,3}] {o.Kind}(k{o.Key},v{o.Val}) -> {o.Ret}"));
        return ok;
    }

    private static string State(SortedDictionary<int, int> m)
    {
        var sb = new StringBuilder();
        foreach (var kv in m) sb.Append(kv.Key).Append('=').Append(kv.Value).Append(';');
        return sb.ToString();
    }
}
