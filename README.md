# Mobratil.Collections

Thread-safe, **sorted, navigable** dictionaries for .NET — the ordered, range-queryable
counterparts to `ConcurrentDictionary`.

[![ci](https://github.com/mobratil/concurrent-ordered-collections/actions/workflows/ci.yml/badge.svg)](https://github.com/mobratil/concurrent-ordered-collections/actions/workflows/ci.yml)

| Type | Backing structure | Best at |
|------|-------------------|---------|
| `ConcurrentSkipListDictionary<TKey,TValue>` | lock-free skip list (a port of Doug Lea's `ConcurrentSkipListMap`) | read-mostly, simple to reason about |
| `ConcurrentBTreeDictionary<TKey,TValue>` | optimistic-lock-coupling (OLC) B+ tree | cache-friendly scans, compact memory, high read throughput |

Both keep keys in sort order, so on top of the usual dictionary surface they add **navigable
lookups** and **live range views**.

## Install

```
dotnet add package Mobratil.Collections
```

Targets `net8.0` and `net10.0`.

## Quick start

```csharp
using Mobratil.Collections;

var map = new ConcurrentBTreeDictionary<int, string>();   // or ConcurrentSkipListDictionary

map[1] = "one";
map.TryAdd(2, "two");
map.AddOrUpdate(2, "TWO", (k, old) => old.ToUpper());

// navigable lookups
map.TryGetCeiling(2, out var ceil);   // least entry with key >= 2
map.TryGetFloor(5, out var floor);    // greatest entry with key <= 5
map.TryGetHigher(2, out var higher);  // least entry with key  > 2
map.TryGetLower(2, out var lower);    // greatest entry with key  < 2

// live, sorted views (like SortedSet<T>.GetViewBetween)
foreach (var kv in map.GetViewBetween(10, 20)) { /* keys in [10,20) */ }
foreach (var kv in map.Reverse())             { /* whole map, descending */ }
foreach (var kv in map.GetViewFrom(10))       { /* keys >= 10 */ }
foreach (var kv in map.GetViewTo(20))         { /* keys < 20  */ }
```

## Consistency model

- **Point operations** — `TryGetValue`, `TryAdd`, `TryRemove`, the indexer, `GetOrAdd`/`AddOrUpdate`,
  and the navigable `TryGetCeiling/Floor/Higher/Lower` — are **linearizable**.
- **Enumeration and range views** are **weakly consistent** (like `ConcurrentDictionary`'s enumerator):
  they never throw on concurrent modification and always yield keys in order, but are *not* a
  point-in-time snapshot.
- **`Count`** is weakly consistent (striped) and never negative — exact only when quiescent.

Full details, and the internal memory-ordering discipline, are in
[docs/CONCURRENCY.md](https://github.com/mobratil/concurrent-ordered-collections/blob/main/docs/CONCURRENCY.md).

## Correctness

Concurrency correctness depends on the hardware memory model, so this is tested on **both x64 (TSO)
and arm64 (weak memory)** on every push:

- **Highly-parallel stress** — oversubscribed churn asserting value-consistency / sorted / unique /
  bounds / navigable / count invariants, across value-type, reference-type, string, and
  custom-comparer shapes.
- **Model-based linearizability** — random concurrent histories checked (Wing–Gong) against a
  sequential `SortedDictionary` oracle, with a self-test proving the checker rejects non-linearizable
  histories.
- A cross-architecture CI matrix: x64 + arm64 Linux, Apple-silicon, and Windows.

This suite found and fixed real concurrency bugs (including an arm64-only weak-memory tear) before
release. It is strong evidence, not a formal proof.

## Status

`0.1.0` — pre-1.0; the API may still move. Solid on the tested platforms, but treat it accordingly.
An experimental B-link tree lives in the repository for benchmarking and is **not** part of this
package.

A detailed walkthrough of the skip-list internals and a fair .NET-vs-Java benchmark are in
[docs/skiplist-deep-dive.md](https://github.com/mobratil/concurrent-ordered-collections/blob/main/docs/skiplist-deep-dive.md)
(predates the rename; methodology and results still stand).

## License

MIT (see [LICENSE](https://github.com/mobratil/concurrent-ordered-collections/blob/main/LICENSE)).
`ConcurrentSkipListDictionary` ports Doug Lea's public-domain `ConcurrentSkipListMap`; see
[NOTICE](https://github.com/mobratil/concurrent-ordered-collections/blob/main/NOTICE) for attribution.
