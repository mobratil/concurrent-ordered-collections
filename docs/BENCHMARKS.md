# Benchmarks — many-core scaling

Throughput and memory of the three structures (`ConcurrentSkipListDictionary`,
`ConcurrentBTreeDictionary`, and the experimental `BLinkTree`) measured on a large Azure VM with the
[`bench-sweep`](../bench-sweep) harness (server GC, best-of-N, `<long,long>`, order 64).

**Machine:** `Standard_D64s_v5` — Intel Xeon Platinum 8370C (Ice Lake), **32 physical cores / 64 vCPU,
2 NUMA nodes**, 256 GB, Ubuntu 24.04. Throughput is **millions of ops/sec (higher is better)**.
Single session, one machine — reproduce with `bench-sweep`.

## Scaling to 64 threads (1M keys, spanning both sockets)

| op | thr | skiplist | bptree | blink |
|----|----:|---------:|-------:|------:|
| **read**  | 8  | 7.3  | 31.0  | 30.7  |
|           | 16 | 17.2 | 59.6  | 53.3  |
|           | 32 | 31.8 | 102.3 | 111.7 |
|           | 64 | 44.6 | **209.7** | 177.7 |
| **write** | 8  | 4.5  | 16.2  | 16.6  |
|           | 32 | 14.1 | 59.2  | 58.4  |
|           | 64 | 21.9 | 81.3  | **88.5** |
| **mixed** | 8  | 5.4  | 19.9  | 20.0  |
|           | 32 | 17.1 | 71.4  | 72.7  |
|           | 64 | 28.7 | 118.4 | **121.8** |

Reads scale near-linearly through one socket (bptree 1→102 Mops over the first 32 threads), then keep
climbing across the second socket. The trees beat the skip list **~4–5×** at 64 threads.

## Dataset size (Mops/s @ 64 threads)

| op | 1M | 10M | 100M |
|----|---:|----:|-----:|
| read  (skiplist / bptree / blink) | 44.6 / **209.7** / 177.7 | 18.6 / **104.7** / 98.8 | 11.0 / **59.5** / 58.1 |
| write (skiplist / bptree / blink) | 21.9 / 81.3 / **88.5**   | 14.9 / 57.9 / **59.1**  | 10.3 / 27.8 / **42.1** |
| mixed (skiplist / bptree / blink) | 28.7 / 118.4 / **121.8** | 17.5 / 72.5 / **74.0**  | 10.5 / 42.1 / **49.2** |

Throughput drops with size for all (deeper structures, cache/TLB misses), but the trees keep their lead.
At 100M, `BLinkTree` overtakes `ConcurrentBTreeDictionary` on writes (its splits never restart readers and
it does no merge), at a large memory cost (below).

## Memory footprint

| | 10M | 100M | bytes/entry @100M |
|---|----:|-----:|------------------:|
| `ConcurrentBTreeDictionary` | 243 MB | **2.4 GB** | **~24** |
| `ConcurrentSkipListDictionary` | 801 MB | 8.0 GB | ~80 |
| `BLinkTree` (experimental) | 1.44 GB | 14.7 GB | ~146 |

(`<long,long>` is 16 B/entry raw, so the B+ tree packs near-optimally.)

## NUMA

Reads are NUMA-tolerant (pinned-to-one-socket ≈ spanning; the second socket nearly *doubles* read
throughput). Writes/mixed pay a **~20–26% cross-socket penalty** when threads span both NUMA nodes — the
structures are NUMA-oblivious, so hot latch lines / counter stripes bounce over the interconnect.

## Takeaways

- **Reads / read-mostly:** `ConcurrentBTreeDictionary` is fastest and most memory-efficient.
- **Write-heavy at huge scale with spare RAM:** `BLinkTree` leads, but it is experimental and uses ~6× the memory.
- **Skip list** is dominated on both throughput and memory at this scale, but is the simplest to reason about.
