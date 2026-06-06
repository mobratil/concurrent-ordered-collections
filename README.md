# Mobratil.Collections

Thread-safe **sorted, navigable** dictionaries for .NET — the ordered, range-queryable
counterparts to `ConcurrentDictionary`.

[![ci](https://github.com/mobratil/collections/actions/workflows/ci.yml/badge.svg)](https://github.com/mobratil/collections/actions/workflows/ci.yml)

> ⚠️ **Vibe-coded.** This library was designed and implemented largely through AI-assisted ("vibe")
> coding. It is tested hard — highly-parallel stress and model-based linearizability on both x64 and
> arm64, and several real concurrency bugs were found and fixed that way — but it has **not** had a
> line-by-line human audit. It's `0.1.0`; treat it accordingly.

- `ConcurrentSkipListDictionary<TKey,TValue>` — lock-free skip list (a port of Doug Lea's `ConcurrentSkipListMap`).
- `ConcurrentBTreeDictionary<TKey,TValue>` — optimistic-lock-coupling B+ tree (compact, high read throughput).

Both keep keys sorted, so on top of the usual dictionary surface they add navigable lookups and live range views.

## Install

```
dotnet add package Mobratil.Collections
```

Targets `net8.0` and `net10.0`.

## Usage

```csharp
using Mobratil.Collections;

var map = new ConcurrentBTreeDictionary<int, string>();   // or ConcurrentSkipListDictionary
map[1] = "one";
map.TryAdd(2, "two");

map.TryGetCeiling(1, out var e);                  // least entry with key >= 1 (also Floor/Higher/Lower)
foreach (var kv in map.GetViewBetween(1, 9)) { } // keys in [1,9), sorted (also GetViewFrom/GetViewTo)
foreach (var kv in map.Reverse()) { }            // whole map, descending
```

## Guarantees

Point operations (get/add/remove, the indexer, `GetOrAdd`/`AddOrUpdate`, and the navigable lookups) are
**linearizable**. Enumeration and range views are **weakly consistent** — never throw on concurrent
mutation, always sorted, but not a point-in-time snapshot (like `ConcurrentDictionary`'s enumerator).
`Count` is weakly consistent and non-negative. Details:
[docs/CONCURRENCY.md](https://github.com/mobratil/collections/blob/main/docs/CONCURRENCY.md).

## Correctness

Tested on **x64 and arm64** on every push — highly-parallel stress plus model-based linearizability
(checked against a `SortedDictionary` oracle). Strong evidence, not a formal proof.

## Benchmarks

Many-core scaling, measured on `Standard_D64s_v5` — Intel Xeon 8370C (Ice Lake), **32 physical cores /
64 vCPU, 2 NUMA nodes**, via the [`bench-sweep`](bench-sweep) harness (`<long,long>`, order 64, server GC).
Throughput in **Mops/s, higher is better**. One machine, one session.

**Scaling to 64 threads (1M keys):**

| op | threads | skiplist | bptree | blink |
|----|--------:|---------:|-------:|------:|
| read  | 8 | 7.3 | 31.0 | 30.7 |
|       | 32 | 31.8 | 102.3 | 111.7 |
|       | 64 | 44.6 | **209.7** | 177.7 |
| write | 32 | 14.1 | 59.2 | 58.4 |
|       | 64 | 21.9 | 81.3 | **88.5** |
| mixed | 64 | 28.7 | 118.4 | **121.8** |

The trees beat the skip list **~4–5×** at 64 threads; reads scale near-linearly through one socket.

**Throughput vs dataset size (@64 threads, Mops/s)** and **memory footprint**:

| @64 threads | 1M | 10M | 100M | | bytes/entry @100M |
|---|---:|---:|---:|---|---:|
| read · bptree | 209.7 | 104.7 | 59.5 | **bptree** | **~24** |
| read · skiplist | 44.6 | 18.6 | 11.0 | skiplist | ~80 |
| read · blink | 177.7 | 98.8 | 58.1 | blink | ~146 |

`ConcurrentBTreeDictionary` is fastest on reads and packs near-optimally (16 B/entry is the raw payload).
At 100M, write-heavy load, `BLinkTree` pulls ahead (it never restarts readers and does no merge) but at
~6× the memory. Writes/mixed pay a ~20–26% penalty when threads span both NUMA nodes (the structures are
NUMA-oblivious).

## Status

`0.1.0`, pre-1.0 — the API may still move. (An experimental B-link tree lives in the repo for
benchmarking and is not part of the package.)

## License

MIT — see [LICENSE](LICENSE). `ConcurrentSkipListDictionary` ports Doug Lea's public-domain
`ConcurrentSkipListMap`; see [NOTICE](NOTICE).
