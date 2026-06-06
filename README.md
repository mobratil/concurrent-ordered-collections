# Mobratil.Collections

Thread-safe **sorted, navigable** dictionaries for .NET — the ordered, range-queryable
counterparts to `ConcurrentDictionary`.

[![ci](https://github.com/mobratil/concurrent-ordered-collections/actions/workflows/ci.yml/badge.svg)](https://github.com/mobratil/concurrent-ordered-collections/actions/workflows/ci.yml)

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
[docs/CONCURRENCY.md](https://github.com/mobratil/concurrent-ordered-collections/blob/main/docs/CONCURRENCY.md).

## Correctness

Tested on **x64 and arm64** on every push — highly-parallel stress plus model-based linearizability
(checked against a `SortedDictionary` oracle). Strong evidence, not a formal proof.

## Status

`0.1.0`, pre-1.0 — the API may still move. (An experimental B-link tree lives in the repo for
benchmarking and is not part of the package.)

## License

MIT — see [LICENSE](LICENSE). `ConcurrentSkipListDictionary` ports Doug Lea's public-domain
`ConcurrentSkipListMap`; see [NOTICE](NOTICE).
