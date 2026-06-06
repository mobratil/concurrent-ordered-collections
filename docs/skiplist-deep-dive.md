> NOTE: Historical deep-dive. This document predates the API rename and the
> single-package restructure; its code samples use the old type/method names
> (LockFreeSkipListDictionary, SubMap, Merge, ...). The algorithm, benchmark
> methodology, and results still stand. For current usage see the top-level
> README and docs/CONCURRENCY.md.

# LockFreeSkipListDictionary — a lock-free `ConcurrentSkipListMap` for .NET

A lock-free, concurrent, **sorted** dictionary for .NET — the structure that Java has as
`java.util.concurrent.ConcurrentSkipListMap` but that the BCL never shipped
(`ConcurrentDictionary` is hash-based and unordered; `SortedDictionary` is not
thread-safe).

This is a faithful port of **Doug Lea's CAS-based skip-list algorithm** (the exact
algorithm backing Java's `ConcurrentSkipListMap`), with one deliberate change for
the CLR: it is **fully generic over `<TKey,TValue>`**, so neither keys nor values are
boxed.

```
src/LockFreeSkipList/LockFreeSkipListDictionary.cs   the data structure
tests/LockFreeSkipList.Tests/                 correctness + parallel-integrity tests (xUnit)
bench/LockFreeSkipList.Bench/                 .NET benchmark harness
java/SkipListBench.java                        Java benchmark harness (mirror image)
run-benchmark.sh                               runs both, prints a combined table
```

---

## 1. The data structure

### Progress guarantee: lock-free

No operation ever takes a lock. Every mutation is published by a single
`Interlocked.CompareExchange` (CAS). A thread that stumbles onto a half-finished
deletion **helps complete it** before continuing (the classic "helping" pattern),
so the system as a whole always makes progress even if individual threads stall.

### How it works (and how the Java algorithm was adapted)

Doug Lea's node has a single `volatile Object value` field that carries **three**
states by reference identity:

| state | Java encoding | meaning |
|-------|---------------|---------|
| live  | the boxed value | a real mapping |
| deleted | `null` | logically removed (tombstone pending) |
| marker | `this` (self-reference) | a deletion marker node |

That trick relies on `Object` and reference identity — which is also exactly why
Java **boxes** every `int`/`long` key and value.

To keep that algorithm but avoid boxing on .NET, the value field stays `object?` and a
live value is encoded one of two ways, chosen as a per-instantiation constant (the JIT
keeps only the relevant branch):

- **`TValue` is a reference type** → the value reference is stored **directly** in the
  field (it's already a CAS-able object). No wrapper, **zero allocation per put**. Null
  values are rejected (null is the "deleted" sentinel) — exactly as Java's CSLM rejects
  null values.
- **`TValue` is a value type** → it's wrapped in a tiny `ValueHolder` whose `TValue`
  field is **not boxed**. One small holder per stored value is the price of an
  atomically-replaceable value cell without boxing (Java pays the same as a `Long` box).

| state | .NET encoding |
|-------|---------------|
| live  | the value reference (ref types) **or** a `ValueHolder` (value types) |
| deleted | `null` |
| marker | the node itself |
| header | a private `BaseHeader` sentinel |

Liveness is decided by **reference identity** against the three sentinels, so it works
even for the awkward `TValue == object` case (where a value can't be told apart from a
sentinel by type alone).

**Allocation profile.** Lookups and the navigable queries allocate **nothing**. For
reference-typed values, overwrites and no-op `TryAdd`s also allocate nothing (the value
cell is the user's own reference, allocated lazily only at the commit point); an insert
allocates the `Node` (+ ~¼ of inserts an index tower). For value-typed values, each
stored value additionally costs one `ValueHolder`. Removal allocates one tombstone
marker node — inherent to the lock-free algorithm, and Java's CSLM pays it too.

Keys are stored directly in a typed `Node.Key` field — **never boxed**, compared via
`IComparer<TKey>`. Index levels above the base list are chosen with a per-thread
xorshift RNG (no shared RNG state, decorrelated across threads), matching the
geometric distribution Lea uses (~¼ of nodes get an index tower).

### Linearizability

Every public operation has a single linearization point — the successful CAS that
publishes its effect, or the volatile read of the value field for lookups. Readers
never block writers and vice-versa; enumeration is **weakly consistent** (never
throws under concurrent mutation, always reflects a valid linearization, always
ascending), exactly like `ConcurrentSkipListMap`'s iterators.

### API

It implements the standard BCL interfaces — `IDictionary<TKey,TValue>`,
`IReadOnlyDictionary<TKey,TValue>`, `ICollection<KeyValuePair<…>>`,
`IEnumerable<KeyValuePair<…>>` — so it drops into any code expecting a dictionary, and
its method surface mirrors `ConcurrentDictionary<TKey,TValue>` (plus sorted-order extras):

```csharp
var dict = new LockFreeSkipListDictionary<long, string>();

dict[1] = "a";                         // insert or overwrite — the .NET "put" (indexer set)
bool added = dict.TryAdd(2, "b");      // insert-if-absent
string v0  = dict.GetOrAdd(3, "c");    // return existing, else add and return
bool upd   = dict.TryUpdate(2, "B", "b");                     // CAS: set to "B" iff current == "b"
string cur = dict.AddOrUpdate(2, "x", (k, old) => old + "!"); // add if absent, else transform

bool ok    = dict.TryGetValue(1, out var v);
bool has   = dict.ContainsKey(1);
string g   = dict[1];                   // getter throws KeyNotFoundException if absent

bool gone  = dict.TryRemove(2, out var old);
bool cond  = dict.TryRemove(new KeyValuePair<long, string>(1, "a")); // remove iff value matches

var first  = dict.TryGetFirst(out var lo); // smallest entry (sorted order)
var last   = dict.TryGetLast(out var hi);  // largest entry
foreach (var kv in dict) { /* ascending order */ }
int n = dict.Count;                    // O(n) snapshot, like ConcurrentSkipListMap.size()
dict.Clear();                          // atomic reset to empty
```

**Navigable (sorted) operations** — the `NavigableMap`/`SortedMap` surface:

```csharp
dict.TryGetFloor(k, out var e);    // greatest entry ≤ k     (floorEntry)
dict.TryGetCeiling(k, out var e);  // least entry ≥ k         (ceilingEntry)
dict.TryGetLower(k, out var e);    // greatest entry < k      (lowerEntry)
dict.TryGetHigher(k, out var e);   // least entry > k         (higherEntry)
dict.TryGetFloorKey(k, out var key);                      // …Key variants too
dict.TryRemoveFirst(out var min);  // atomic remove-min       (pollFirstEntry)
dict.TryRemoveLast(out var max);   // atomic remove-max       (pollLastEntry)

// Live, navigable sub-views (reflect & can mutate the parent within range):
RangeView head = dict.HeadMap(10);              // keys < 10
RangeView tail = dict.TailMap(10);              // keys ≥ 10
RangeView mid  = dict.SubMap(3, true, 9, false);// [3, 9)
RangeView rev  = dict.DescendingMap();          // reverse order
foreach (var k in dict.DescendingKeys) { … }

// ConcurrentDictionary-style helpers also present:
dict.TryUpdate(k, newV, expectedV);   dict.AddOrUpdate(k, addV, (key,old) => …);
dict.GetValueOrDefault(k, fallback);  dict.Merge(k, v, (old,given) => …);
dict.ComputeIfAbsent(k, key => …);    dict.ComputeIfPresent(k, (key,old) => …);
dict.ContainsValue(v);  dict.PutAll(entries);  dict.ReplaceAll((key,v) => …);
```

A custom ordering is supported via `new LockFreeSkipListDictionary<K,V>(IComparer<K>)`,
exposed as `dict.Comparer`. The full Java `ConcurrentSkipListMap` method surface is
covered (navigable queries, polling, range/descending views, and the functional
update helpers) — see the API parity table in the source XML docs.

---

## 2. Correctness & integrity testing

`dotnet test` runs 64 tests — interface-conformance checks through `IDictionary`/
`IReadOnlyDictionary`/`ICollection` references, navigable-query checks against a sorted
oracle, range/descending-view behaviour, concurrent remove-min/max linearizability,
**concurrent `RangeView` writes vs. parent reads (and vice-versa)**, lost-update-free
`AddOrUpdate`/`Merge` counters under contention, and allocation assertions
(reads/overwrites/no-op adds allocate zero for reference values, incl. the awkward
`object` value type and null-value rejection). Beyond the obvious single-threaded cases,
correctness is pinned down two ways:

**Model-based testing** — 20,000 randomized operations (indexer set/`TryAdd`/`TryRemove`/
`TryGetValue`) are run against both the skip list and a reference
`SortedDictionary`, asserting identical return values at every step and full
structural equivalence at the end, across multiple seeds.

**Parallel-integrity testing** — these are written so the *expected final state is
deterministic* even though the interleaving is not, so they assert exact
correctness, not merely "didn't crash":

| test | what it proves |
|------|----------------|
| `Disjoint_Parallel_Inserts_Preserve_All_Entries` | N threads insert disjoint ranges → union is complete, sorted, correct values, exact count |
| `Concurrent_TryAdd_Of_Same_Keys_Succeeds_Exactly_Once` | all threads race to add the same keys → each key inserted **exactly once** (linearizable insert, no duplicates) |
| `Concurrent_Remove_Of_Same_Keys_Succeeds_Exactly_Once` | all threads race to remove the same keys → each key removed **exactly once** (no double-remove, no lost remove) |
| `Mixed_Churn_Then_Deterministic_Drain_Leaves_Exact_Survivors` | heavy random add/remove churn, then a deterministic drain → survivors are *exactly* the untouched set, still sorted |
| `Enumeration_Is_Always_Sorted_Under_Concurrent_Mutation` | writers mutate while a reader enumerates for 2 s → enumeration never throws and is always strictly ascending |
| `Producer_Consumer_Every_Key_Consumed_Exactly_Once` | producers insert, consumers remove → the multiset of removed keys equals the produced set exactly |
| `Hot_Small_Keyset_Exercises_Markers_And_Helping` | 64 keys hammered by all cores with add/remove → maximises concurrent deletes on the same nodes (the marker + helping machinery), then asserts a valid sorted set |
| `Clear_Under_Concurrent_Writers_Never_Corrupts` | repeated `Clear()` while all cores mutate → enumeration stays sorted, never corrupts |
| `GetOrAdd_Is_Atomic_Under_Contention` | all threads race `GetOrAdd` on the same keys → every thread observes the same winning value per key |
| `Concurrent_PollFirst_…` / `…PollFirst_And_PollLast_…` | poll-min/-max under contention → every key claimed exactly once, never by both ends |
| `Concurrent_AddOrUpdate_And_Merge_Counters_Lose_No_Updates` | all cores increment shared keys via `AddOrUpdate`/`Merge` → each final value equals the exact increment count (CAS loops lose nothing) |
| `View_Writes_Plus_Dictionary_Reads_Leave_Out_Of_Range_Untouched` | writers mutate **only through a `SubMap`**, readers read the parent → out-of-range keys stay byte-for-byte intact; deterministic drain leaves exactly the out-of-range set |
| `Dictionary_Writes_Plus_View_Reads_Stay_Sorted_And_In_Range` | parent hammered across the whole key space while a view is enumerated → the view is always strictly ascending and strictly within bounds, never throws |
| `Descending_View_Stays_Descending_Under_Concurrent_Mutation` | reversed view enumerated under churn → always strictly descending and in range |
| `Concurrent_View_TryAdd_Succeeds_Exactly_Once` | all threads insert the same keys **through the view** → exactly once each, nothing leaks outside the range |
| `Interleaved_View_And_Parent_Writes_Keep_Structure_Consistent` | view writes and parent writes interleaved on overlapping keys → structure stays sorted/unique; the view reports exactly the in-range slice of the parent |

```bash
dotnet test -c Release
```

---

## 3. Benchmark: .NET vs Java — made fair

### Why fairness needs care

Java generics are erased and operate on `Object`, so
`ConcurrentSkipListMap<Long,Long>` **boxes every key and value** into heap objects.
A naïve comparison against a .NET `<long,long>` map would conflate two different
things: (a) the runtime + algorithm, and (b) the boxing/allocation cost the JVM
pays that .NET does not. So the harness runs **three** configurations:

| config | keys/values | boxing? |
|--------|-------------|---------|
| `dotnet-long-long` | `long` / `long` | none — value types inline (idiomatic .NET) |
| `dotnet-ref-ref`   | `LongRef` / `LongRef` (a class wrapping a `long`) | one heap object per key/value, **matched to Java's autoboxing** |
| `java-cslm`        | `Long` / `Long` | JVM autoboxing |

`dotnet-ref-ref` vs `java-cslm` isolates **runtime + algorithm** under identical
allocation behaviour. `dotnet-long-long` then shows the **additional** win .NET gets
from value types — the actual reason you'd reach for this in .NET.

### What's held identical

- **Same workload generator**: a `SplitMix64` PRNG, implemented bit-identically in
  both languages, seeded identically per thread — so every thread executes the
  *exact same operation stream* in both runtimes.
- Same key range (2,000,000), same pre-fill (1,000,000 entries), same op mix
  (80% get / 18% put / 2% remove), same thread counts (1/2/4/8), same warmup (3) and
  measured (5, median reported) iterations.
- Threads start together behind a barrier; only steady-state is timed.
- **GC**: .NET runs **Server GC + concurrent**, the throughput-oriented counterpart
  to the JVM's default concurrent **G1**.

### Run it

```bash
./run-benchmark.sh           # large working set, 1M entries (a few minutes)
./run-benchmark.sh --small   # cache-resident working set, 100k entries
./run-benchmark.sh --ops     # per-operation breakdown (get/update/insert/remove)
./run-benchmark.sh --rw      # W-writers × R-readers interference matrix
./run-benchmark.sh --quick   # fast smoke run (combine with --ops / --rw)
```

### Results

> Measured on: Apple Silicon, 10 cores, .NET 10 (Server GC) vs OpenJDK 26 (G1).
> Throughput in **millions of operations/second — higher is better**, median of 5
> measured iterations after 3 warmups. Reproduce with `./run-benchmark.sh` and
> `./run-benchmark.sh --small`.

Two working-set sizes are reported, because they stress different things:

**A. Cache-resident working set** (`--small`: 100k entries) — the tree mostly fits
in cache, so per-operation **allocation** is the dominant cost. Median Mops/s
(**bold** = fastest in row):

| threads | `dotnet-long-long`<br>(no boxing) | `dotnet-ref-ref`<br>(ref keys+values) | `java-cslm`<br>(JVM boxes) |
|:-------:|:--------------------------------:|:------------------------------------:|:-------------------------:|
|    1    |             **2.25**             |                1.52                  |           1.61            |
|    2    |             **4.21**             |                2.97                  |           3.24            |
|    4    |             **7.74**             |                5.88                  |           7.35            |
|    8    |              9.52                |                7.57                  |          **10.04**        |

**B. Large working set** (default: 1M entries) — pointer-chasing dominates, the
structure is **memory-latency bound**. Median Mops/s:

| threads | `dotnet-long-long` | `dotnet-ref-ref` | `java-cslm` |
|:-------:|:------------------:|:----------------:|:-----------:|
|    1    |        0.61        |       0.62       |  **0.97**   |
|    2    |        1.38        |       1.26       |  **1.81**   |
|    4    |        2.59        |       2.51       |  **3.44**   |
|    8    |        3.76        |       4.45       |  **5.49**   |

#### How to read it

- **`dotnet-long-long`** — value-type keys and values: **zero per-operation
  allocation** (lookups, overwrites, no-op adds all allocate nothing; only a genuine
  insert allocates the node). The idiomatic .NET choice.
- **`dotnet-ref-ref`** — reference-typed keys and values: the closest .NET analogue to
  Java's allocate-a-box-per-operation pattern (a wrapper per lookup key / per insert).
- **`java-cslm`** — `ConcurrentSkipListMap<Long,Long>`, autoboxing keys and values.

#### Takeaways (honest)

1. **When allocation is the bottleneck (cache-resident), value types win.**
   `dotnet-long-long` is fastest at 1–4 threads — clearly ahead of Java (e.g. 7.74 vs
   7.35 at 4 threads) — and within ~5% at 8. `dotnet-ref-ref`, which allocates a
   wrapper per op, is consistently slowest (~20% behind `long-long`): the allocation
   tax made visible.

2. **When memory latency is the bottleneck (large set), Java's implementation leads.**
   `java-cslm` is ~15–45% ahead across the board. Here the no-allocation dividend
   washes out — the structure spends its time chasing cold pointers, not in the
   allocator — so `dotnet-long-long` ≈ `dotnet-ref-ref`, and both trail Java's mature,
   ~20-year-tuned algorithm (helped further by C2 escape-analysis on its read-side key
   boxes).

3. **Both scale near-linearly** to 4 threads, tapering at 8 as the read-heavy mix
   saturates memory bandwidth and shared cache lines.

> **Bottom line for the original ask:** .NET never had a `ConcurrentSkipListMap`;
> this fills that gap with a correct, lock-free implementation that **beats Java's
> when the working set is cache-resident** (value types → no allocator pressure) and
> **trails it by ~15–45% when memory-latency-bound** (raw algorithm maturity). Numbers
> are from one machine/session — reproduce with `./run-benchmark.sh`.

### Per-operation breakdown (`--ops`)

The mixed numbers above blend operations. This isolates each one — every operation
runs on a map prepared specifically for it, so you see the cost of that single path.
`./run-benchmark.sh --ops`. Median Mops/s (higher is better):

- **get/update** prep: a 500k-entry tree of the EVEN keys. `get-hit` looks up evens
  (present); `get-miss` looks up odds (absent, *scattered between* present nodes — a
  realistic miss); `update` overwrites evens.
- **insert/remove**: 2,000,000 keys split into disjoint contiguous per-thread ranges
  (insert into an empty map; remove from a pre-filled one).

| op | threads | `dotnet-long-long` | `dotnet-ref-ref` | `java-cslm` |
|----|:-------:|:------------------:|:----------------:|:-----------:|
| **get-hit**  | 1 |  2.17 |  1.93 |  1.98 |
|              | 2 |  4.98 |  3.92 |  4.35 |
|              | 4 |  9.62 |  8.10 |  7.45 |
|              | 8 | **12.62** | 11.87 | 10.26 |
| **get-miss** | 1 |  2.20 |  2.44 |  1.84 |
|              | 2 |  5.06 |  4.40 |  3.62 |
|              | 4 |  9.57 |  9.11 |  6.56 |
|              | 8 | **12.35** | 12.20 | 10.19 |
| **update**   | 1 |  2.18 |  2.17 |  1.72 |
|              | 2 |  4.19 |  4.23 |  3.72 |
|              | 4 |  8.16 |  7.68 |  7.81 |
|              | 8 | 10.96 |  9.06 | 10.70 |
| **insert**   | 1 | **13.07** | 12.27 |  9.35 |
|              | 2 | **22.73** | 18.87 | 15.87 |
|              | 4 | 40.00 | 39.22 | 33.33 |
|              | 8 | 46.51 | 46.51 | 45.45 |
| **remove**   | 1 | **19.80** | 16.13 | 15.38 |
|              | 2 | **29.41** | 21.05 | 20.62 |
|              | 4 | 33.33 | 27.03 | 31.75 |
|              | 8 | **48.78** | 47.62 | 38.46 |

#### Reading the per-op numbers

- **insert/remove are much faster than get/update** — but that's the *access pattern*,
  not the operation: insert/remove here use disjoint **sequential** ranges (each key
  near the last → hot cache, predecessor already loaded), whereas get/update do
  **random** access into a large tree (cold pointer-chasing). Don't compare insert
  Mops/s to get Mops/s directly; compare each operation *across the three configs*.

- **Lookups (`get-*`):** `dotnet-long-long` is fastest, pulling ahead as threads grow
  (12.62 vs Java 10.26 at 8 threads) — reads allocate nothing on the .NET side.
  `update` is a near-tie (memory-latency bound, the holder write is cheap).

- **Writes (`insert`/`remove`):** `dotnet-long-long` leads at low thread counts
  (insert 13.07 vs Java 9.35; remove clearly ahead throughout), with insert converging
  across all three at 8 threads as the sequential write pattern saturates.

- **The allocation optimization is visible here.** With reference values now stored
  **inline** (no per-put holder), `dotnet-ref-ref` insert/remove **scale cleanly to 8
  threads** (insert 46.51, remove 47.62) — the earlier version, which allocated an
  extra holder per put, *collapsed* at 8 threads (insert ~20) once the allocation rate
  overwhelmed the GC. Removing that allocation removed the cliff.

### Reader/writer interference matrix (`--rw`)

Separate, dedicated threads per role running **at the same time**: **W writer threads**
(each 50% `put` / 50% `remove`) churning the map while **R reader threads** do `get`s.
Each cell runs for 1 s; we report the **read** and **write** throughput of that mix
separately. `./run-benchmark.sh --rw`. (500k even-key tree; readers hit ~50%.)
Rows = writers **W**, columns = readers **R**. Median Mops/s.

Each cell = *that many writer threads and reader threads running together*. Move
**right** for more readers, **down** for more writers.

**`dotnet-long-long` — READ throughput (Mops/s)**

| Writers ↓ \ Readers → | R=1 | R=2 | R=4 | R=8 |
|:--:|:--:|:--:|:--:|:--:|
| **W=1** | 2.14 | 3.95 | 6.87 | 10.41 |
| **W=2** | 2.17 | 4.06 | 6.31 | 9.49 |
| **W=4** | 1.71 | 3.15 | 5.79 | 8.92 |
| **W=8** | 1.37 | 2.59 | 4.27 | 6.97 |

**`dotnet-long-long` — WRITE throughput (Mops/s)**

| Writers ↓ \ Readers → | R=1 | R=2 | R=4 | R=8 |
|:--:|:--:|:--:|:--:|:--:|
| **W=1** | 2.17 | 1.99 | 1.71 | 1.19 |
| **W=2** | 4.23 | 3.88 | 2.96 | 2.21 |
| **W=4** | 6.94 | 6.24 | 5.56 | 4.29 |
| **W=8** | 10.74 | 10.21 | 8.71 | 6.95 |

**`java-cslm` — READ throughput (Mops/s)**

| Writers ↓ \ Readers → | R=1 | R=2 | R=4 | R=8 |
|:--:|:--:|:--:|:--:|:--:|
| **W=1** | 2.34 | 4.74 | 7.77 | 11.59 |
| **W=2** | 2.30 | 4.42 | 7.79 | 11.39 |
| **W=4** | 2.06 | 3.58 | 6.24 | 9.26 |
| **W=8** | 1.48 | 2.72 | 4.66 | 7.05 |

**`java-cslm` — WRITE throughput (Mops/s)**

| Writers ↓ \ Readers → | R=1 | R=2 | R=4 | R=8 |
|:--:|:--:|:--:|:--:|:--:|
| **W=1** | 2.27 | 2.19 | 1.81 | 1.38 |
| **W=2** | 4.32 | 4.16 | 3.67 | 2.61 |
| **W=4** | 7.67 | 6.84 | 5.83 | 4.36 |
| **W=8** | 11.45 | 10.59 | 9.12 | 6.88 |

Example: **W=4, R=8** = 4 writers + 8 readers concurrently → in `dotnet-long-long`
the readers do **8.92** Mops/s while the writers do **4.29** Mops/s. Java leads most
cells here (memory-latency-bound regime), but both degrade gracefully and never starve.
(Full data incl. `dotnet-ref-ref` is printed by the command.)

#### 3D surfaces

The same matrix as throughput surfaces — READ rises toward more readers, WRITE rises
toward more writers, each dips along the other axis (contention):

![Reader/writer throughput surfaces](viz/rw_surfaces.png)

Rotatable/zoomable version: **[`viz/rw_surfaces.html`](viz/rw_surfaces.html)**
(open in a browser). Regenerate everything with `./viz/plot.sh` — see
[`viz/README.md`](viz/README.md).

#### Per-thread scalability (2D)

Aggregate throughput **÷ the number of threads of that role** — i.e. how much each
*individual* reader/writer thread gets. **A flat line means perfect scaling** (every
added thread pulls its weight); a **downward slope means contention**. Top row: read
Mops per reader thread (one line per writer count W); bottom row: write Mops per writer
thread (one line per reader count R).

![Per-thread throughput / scalability](viz/rw_perthread.png)

Interactive: **[`viz/rw_perthread.html`](viz/rw_perthread.html)**. Per-thread read
efficiency starts at ~2–2.4 Mops/s and degrades gently as either role adds threads
(contention) — no cliff. The three implementations track closely here, with Java a
touch higher (this is the memory-latency-bound regime, consistent with the large-set
results above).

#### What the matrix shows

- **Readers and writers make progress simultaneously** — this is lock-free, so readers
  never block on writers or vice-versa. Read throughput scales **across a row** (more
  **R**eaders), write throughput scales **down a column** (more **W**riters).

- **Cross-role interference is real but graceful.** Adding writers erodes read
  throughput (long-long read column R=8: 10.41 → 9.49 → 8.92 → 6.97 as W goes 1→8) and
  adding readers erodes write throughput (long-long write row W=8: 10.74 → 10.21 → 8.71
  → 6.95 as R goes 1→8) — they compete for cores and invalidate each other's cache
  lines, but nothing starves or collapses.

- **`java-cslm` leads most cells, `dotnet-long-long` stays within ~10–15%** — this is
  the memory-latency-bound regime (500k-entry tree, random access), where Java's mature
  algorithm wins (consistent with the large-set mixed results). At the oversubscribed
  corner (W=8, R=8 = 16 threads on 10 cores) they converge: 6.97/6.95 vs Java 7.05/6.88.

- **W+R > cores (10)** cells are deliberately included (e.g. 8×8 = 16 threads): they
  show graceful degradation under oversubscription, not a cliff.

---

## Build requirements

- .NET 10 SDK
- A JDK (any modern one) for the Java benchmark — `brew install openjdk`

A repo-local `nuget.config` pins restore to nuget.org so the build works regardless
of machine-wide private feeds.
