using Xunit;
using Xunit.Abstractions;

namespace LockFreeSkipList.Tests;

/// <summary>
/// The torn-read bug was only caught because the suite asserts value-consistency. Those runs used
/// value-type &lt;int,int&gt; with value==key. This class widens the shape coverage to the read paths
/// the int tests never exercised:
///   * REFERENCE-type values (object[] Values array) — a torn read yields a neighbour's string.
///   * REFERENCE-type keys (string comparison + storage).
///   * value != key and a non-trivial value→key mapping, so a torn pair is unambiguous.
///   * a CUSTOM comparer (delegate path, not the JIT-specialised default).
/// Runs against skip list + bptree (B-link excluded, unmaintained). Oversubscribed; STRESS_SECONDS scales.
/// </summary>
[Collection("stress")]
public class ValueShapeStressTests
{
    private readonly ITestOutputHelper _out;
    public ValueShapeStressTests(ITestOutputHelper output) => _out = output;

    private static int Threads => Math.Max(8, Environment.ProcessorCount * 2);
    private static double Seconds =>
        double.TryParse(Environment.GetEnvironmentVariable("STRESS_SECONDS"), out var s) && s > 0 ? s : 1.5;
    private static DateTime Deadline => DateTime.UtcNow.AddSeconds(Seconds);

    public static IEnumerable<object[]> Matrix()
    {
        yield return new object[] { "skiplist", 0 };
        foreach (var order in new[] { 4, 8, 64 })
            yield return new object[] { "bptree", order };
    }

    private static INavMap<int, string> NewIntStr(string kind, int order) => kind switch
    {
        "skiplist" => new SkipNavMap<int, string>(),
        "bptree" => new BPlusNavMap<int, string>(order),
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    private static INavMap<string, string> NewStrStr(string kind, int order) => kind switch
    {
        "skiplist" => new SkipNavMap<string, string>(),
        "bptree" => new BPlusNavMap<string, string>(order),
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    private static List<Task> Spawn(int n, Action<int> body)
    {
        var t = new List<Task>(n);
        for (int i = 0; i < n; i++) { int id = i; t.Add(Task.Factory.StartNew(() => body(id), TaskCreationOptions.LongRunning)); }
        return t;
    }
    private static void StopAll(CancellationTokenSource s, List<Task> t) { s.Cancel(); Task.WaitAll(t.ToArray()); }

    // Reference-type value: a torn read pairs a key with another key's string -> caught by the value check.
    private static string V(int k) => $"v{k}";

    [Theory]
    [MemberData(nameof(Matrix))]
    public void RefValue_Scan_Stays_Consistent_Under_Churn(string kind, int order)
    {
        var d = NewIntStr(kind, order);
        const int n = 50_000;
        for (int k = 0; k < n; k++) d[k] = V(k);

        using var stop = new CancellationTokenSource();
        var writers = Spawn(Threads, tid =>
        {
            var rng = new Random(tid * 7 + 1);
            while (!stop.IsCancellationRequested)
            {
                int k = rng.Next(n);
                if ((rng.Next() & 1) == 0) d[k] = V(k); else d.TryRemove(k, out _);
            }
        });

        long scans = 0;
        for (var end = Deadline; DateTime.UtcNow < end;)
        {
            int prev = int.MinValue;
            foreach (var kv in d)
            {
                Assert.True(kv.Key > prev, $"[{kind}/{order}] order/dup: {kv.Key} after {prev}");
                Assert.True(kv.Value == V(kv.Key), $"[{kind}/{order}] TORN ref value: key {kv.Key} -> '{kv.Value}'");
                prev = kv.Key;
            }
            scans++;
        }
        StopAll(stop, writers);
        Assert.True(scans > 0);
        d.Validate();
        _out.WriteLine($"[{kind}/{order}] {scans} ref-value scans");
    }

    [Theory]
    [MemberData(nameof(Matrix))]
    public void RefKey_And_Value_Scan_Sorted_And_Consistent(string kind, int order)
    {
        var d = NewStrStr(kind, order);
        const int n = 40_000;
        string K(int i) => $"k{i:D6}";          // zero-padded so string order == numeric order
        for (int i = 0; i < n; i++) d[K(i)] = V(i);

        using var stop = new CancellationTokenSource();
        var writers = Spawn(Threads, tid =>
        {
            var rng = new Random(tid * 13 + 5);
            while (!stop.IsCancellationRequested)
            {
                int i = rng.Next(n);
                if ((rng.Next() & 1) == 0) d[K(i)] = V(i); else d.TryRemove(K(i), out _);
            }
        });

        long scans = 0;
        for (var end = Deadline; DateTime.UtcNow < end;)
        {
            string prev = "";
            foreach (var kv in d)
            {
                Assert.True(string.CompareOrdinal(kv.Key, prev) > 0, $"[{kind}/{order}] str order/dup: {kv.Key} after {prev}");
                // value 'v{i}' must match the key 'k{i:D6}' it is paired with
                int idx = int.Parse(kv.Key.Substring(1));
                Assert.True(kv.Value == V(idx), $"[{kind}/{order}] TORN str pair: {kv.Key} -> '{kv.Value}'");
                prev = kv.Key;
            }
            scans++;
        }
        StopAll(stop, writers);
        Assert.True(scans > 0);
        d.Validate();
        _out.WriteLine($"[{kind}/{order}] {scans} str-key scans");
    }

    [Theory]
    [MemberData(nameof(Matrix))]
    public void CustomComparer_NoCorruption_Under_Churn(string kind, int order)
    {
        // A delegate comparer (NOT Comparer<T>.Default) exercises the non-specialised compare path.
        var cmp = Comparer<int>.Create((a, b) => a.CompareTo(b));
        INavMap<int, string> d = kind switch
        {
            "skiplist" => new SkipNavMap<int, string>(cmp),
            "bptree" => new BPlusNavMap<int, string>(order, cmp),
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
        const int n = 40_000;
        for (int k = 0; k < n; k++) d[k] = V(k);

        using var stop = new CancellationTokenSource();
        var writers = Spawn(Threads, tid =>
        {
            var rng = new Random(tid * 17 + 3);
            while (!stop.IsCancellationRequested)
            {
                int k = rng.Next(n);
                if ((rng.Next() & 1) == 0) d[k] = V(k); else d.TryRemove(k, out _);
            }
        });

        long scans = 0;
        for (var end = Deadline; DateTime.UtcNow < end;)
        {
            int prev = int.MinValue;
            foreach (var kv in d)
            {
                Assert.True(kv.Key > prev, $"[{kind}/{order}] order/dup: {kv.Key} after {prev}");
                Assert.True(kv.Value == V(kv.Key), $"[{kind}/{order}] TORN value (custom cmp): {kv.Key} -> '{kv.Value}'");
                prev = kv.Key;
            }
            scans++;
        }
        StopAll(stop, writers);
        Assert.True(scans > 0);
        d.Validate();
    }
}
