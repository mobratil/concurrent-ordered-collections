using System;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Ordered;

/// <summary>
/// A LongAdder-style striped counter. Instead of one shared atomic (whose cache line ping-pongs between
/// cores under concurrent writers), it keeps an array of sub-counters on SEPARATE cache lines; each
/// thread bumps the stripe its id hashes to, so concurrent Increment/Decrement rarely touch the same
/// line. <see cref="Sum"/> is O(stripes) and a weakly-consistent snapshot under concurrency (exact when
/// quiescent), exactly like <c>java.util.concurrent.atomic.LongAdder</c>.
/// </summary>
internal sealed class StripedCounter
{
    private const int Pad = 16;                 // 16 longs = 128 bytes between live slots -> no false sharing
    private readonly long[] _cells;
    private readonly int _mask;                 // stripes - 1 (stripes is a power of two)

    public StripedCounter()
    {
        int stripes = 4;
        while (stripes < Environment.ProcessorCount) stripes <<= 1;   // >= cores, power of two
        _mask = stripes - 1;
        _cells = new long[(stripes + 1) * Pad];                       // +1 leading guard so slot 0 clears the header line
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int Slot()
    {
        uint h = (uint)Environment.CurrentManagedThreadId * 2654435761u;   // Knuth multiplicative hash
        return Pad + ((int)(h >> 15) & _mask) * Pad;                       // leading guard + strided stripe
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Increment() => Interlocked.Increment(ref _cells[Slot()]);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Decrement() => Interlocked.Decrement(ref _cells[Slot()]);

    /// <summary>Sum of all stripes. Not an atomic snapshot under concurrent updates (like LongAdder.sum()); exact when quiescent.</summary>
    public long Sum()
    {
        long s = 0;
        for (int i = Pad; i < _cells.Length; i += Pad) s += Interlocked.Read(ref _cells[i]);
        return s;
    }

    /// <summary>Resets to an exact value. Quiescent semantics (used by Clear/Compact).</summary>
    public void Set(long value)
    {
        for (int i = Pad; i < _cells.Length; i += Pad) Volatile.Write(ref _cells[i], 0L);
        Volatile.Write(ref _cells[Pad], value);
    }
}
