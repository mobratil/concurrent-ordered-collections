using System.Collections;
using Mobratil.Collections;

namespace LockFreeSkipList.Tests;

// =====================================================================
//  Uniform adapters so one parameterized stress suite runs identically
//  against all three concurrent ordered maps. The three structures share
//  an identical navigable + range surface but NO common interface (each
//  has its own nested RangeView), so we wrap them here. View writes go
//  through the REAL RangeView methods (bounds-check confinement included),
//  never through a parent shortcut — otherwise the confinement tests would
//  be testing the test, not the structure.
// =====================================================================

public interface INavView<K, V> : IEnumerable<KeyValuePair<K, V>>
{
    V this[K key] { set; }
    bool TryAdd(K key, V value);
    bool Remove(K key);
    bool ContainsKey(K key);
    int Count { get; }
    void Clear();
    bool TryGetCeiling(K key, out KeyValuePair<K, V> e);
    bool TryGetFloor(K key, out KeyValuePair<K, V> e);
    bool TryGetHigher(K key, out KeyValuePair<K, V> e);
    bool TryGetLower(K key, out KeyValuePair<K, V> e);
    INavView<K, V> Reverse();
}

public interface INavMap<K, V> : IEnumerable<KeyValuePair<K, V>>
{
    V this[K key] { get; set; }
    bool TryAdd(K key, V value);
    bool TryRemove(K key, out V value);
    bool ContainsKey(K key);
    bool TryGetValue(K key, out V value);
    int Count { get; }
    bool TryGetCeiling(K key, out KeyValuePair<K, V> e);
    bool TryGetFloor(K key, out KeyValuePair<K, V> e);
    bool TryGetHigher(K key, out KeyValuePair<K, V> e);
    bool TryGetLower(K key, out KeyValuePair<K, V> e);
    INavView<K, V> GetViewBetween(K from, K to);
    INavView<K, V> GetViewBetween(K from, bool fromInc, K to, bool toInc);
    INavView<K, V> GetViewTo(K to, bool inclusive);
    INavView<K, V> GetViewFrom(K from, bool inclusive);
    INavView<K, V> Reverse();
    void Validate();   // structural self-check in a quiescent phase; no-op where unsupported
}

public static class NavMapFactory
{
    public static readonly string[] Kinds = { "skiplist", "bptree", "blink" };

    public static INavMap<int, int> Create(string kind, int order) => kind switch
    {
        "skiplist" => new SkipNavMap<int, int>(),
        "bptree" => new BPlusNavMap<int, int>(order),
        "blink" => new BLinkNavMap<int, int>(order),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "unknown structure")
    };
}

// ---------------- skip list ----------------
internal sealed class SkipNavMap<K, V> : INavMap<K, V> where K : notnull
{
    private readonly ConcurrentSkipListDictionary<K, V> _m;
    public SkipNavMap(IComparer<K>? comparer = null) => _m = new ConcurrentSkipListDictionary<K, V>(comparer ?? Comparer<K>.Default);
    public V this[K key] { get => _m[key]; set => _m[key] = value; }
    public bool TryAdd(K key, V value) => _m.TryAdd(key, value);
    public bool TryRemove(K key, out V value) => _m.TryRemove(key, out value);
    public bool ContainsKey(K key) => _m.ContainsKey(key);
    public bool TryGetValue(K key, out V value) => _m.TryGetValue(key, out value);
    public int Count => _m.Count;
    public bool TryGetCeiling(K key, out KeyValuePair<K, V> e) => _m.TryGetCeiling(key, out e);
    public bool TryGetFloor(K key, out KeyValuePair<K, V> e) => _m.TryGetFloor(key, out e);
    public bool TryGetHigher(K key, out KeyValuePair<K, V> e) => _m.TryGetHigher(key, out e);
    public bool TryGetLower(K key, out KeyValuePair<K, V> e) => _m.TryGetLower(key, out e);
    public INavView<K, V> GetViewBetween(K from, K to) => new SkipNavView<K, V>(_m.GetViewBetween(from, to));
    public INavView<K, V> GetViewBetween(K from, bool fi, K to, bool ti) => new SkipNavView<K, V>(_m.GetViewBetween(from, fi, to, ti));
    public INavView<K, V> GetViewTo(K to, bool inc) => new SkipNavView<K, V>(_m.GetViewTo(to, inc));
    public INavView<K, V> GetViewFrom(K from, bool inc) => new SkipNavView<K, V>(_m.GetViewFrom(from, inc));
    public INavView<K, V> Reverse() => new SkipNavView<K, V>(_m.Reverse());
    public void Validate() { }   // no structural validator on the skip list
    public IEnumerator<KeyValuePair<K, V>> GetEnumerator() => _m.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

internal sealed class SkipNavView<K, V> : INavView<K, V> where K : notnull
{
    private readonly ConcurrentSkipListDictionary<K, V>.RangeView _v;
    public SkipNavView(ConcurrentSkipListDictionary<K, V>.RangeView v) => _v = v;
    public V this[K key] { set => _v[key] = value; }
    public bool TryAdd(K key, V value) => _v.TryAdd(key, value);
    public bool Remove(K key) => _v.Remove(key);
    public bool ContainsKey(K key) => _v.ContainsKey(key);
    public int Count => _v.Count;
    public void Clear() => _v.Clear();
    public bool TryGetCeiling(K key, out KeyValuePair<K, V> e) => _v.TryGetCeiling(key, out e);
    public bool TryGetFloor(K key, out KeyValuePair<K, V> e) => _v.TryGetFloor(key, out e);
    public bool TryGetHigher(K key, out KeyValuePair<K, V> e) => _v.TryGetHigher(key, out e);
    public bool TryGetLower(K key, out KeyValuePair<K, V> e) => _v.TryGetLower(key, out e);
    public INavView<K, V> Reverse() => new SkipNavView<K, V>(_v.Reverse());
    public IEnumerator<KeyValuePair<K, V>> GetEnumerator() => _v.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

// ---------------- OLC B+ tree ----------------
internal sealed class BPlusNavMap<K, V> : INavMap<K, V> where K : notnull
{
    private readonly ConcurrentBTreeDictionary<K, V> _m;
    public BPlusNavMap(int order, IComparer<K>? comparer = null) => _m = new ConcurrentBTreeDictionary<K, V>(order, comparer);
    public V this[K key] { get => _m[key]; set => _m[key] = value; }
    public bool TryAdd(K key, V value) => _m.TryAdd(key, value);
    public bool TryRemove(K key, out V value) => _m.TryRemove(key, out value);
    public bool ContainsKey(K key) => _m.ContainsKey(key);
    public bool TryGetValue(K key, out V value) => _m.TryGetValue(key, out value);
    public int Count => _m.Count;
    public bool TryGetCeiling(K key, out KeyValuePair<K, V> e) => _m.TryGetCeiling(key, out e);
    public bool TryGetFloor(K key, out KeyValuePair<K, V> e) => _m.TryGetFloor(key, out e);
    public bool TryGetHigher(K key, out KeyValuePair<K, V> e) => _m.TryGetHigher(key, out e);
    public bool TryGetLower(K key, out KeyValuePair<K, V> e) => _m.TryGetLower(key, out e);
    public INavView<K, V> GetViewBetween(K from, K to) => new BPlusNavView<K, V>(_m.GetViewBetween(from, to));
    public INavView<K, V> GetViewBetween(K from, bool fi, K to, bool ti) => new BPlusNavView<K, V>(_m.GetViewBetween(from, fi, to, ti));
    public INavView<K, V> GetViewTo(K to, bool inc) => new BPlusNavView<K, V>(_m.GetViewTo(to, inc));
    public INavView<K, V> GetViewFrom(K from, bool inc) => new BPlusNavView<K, V>(_m.GetViewFrom(from, inc));
    public INavView<K, V> Reverse() => new BPlusNavView<K, V>(_m.Reverse());
    public void Validate() => _m.Validate();
    public IEnumerator<KeyValuePair<K, V>> GetEnumerator() => _m.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

internal sealed class BPlusNavView<K, V> : INavView<K, V> where K : notnull
{
    private readonly ConcurrentBTreeDictionary<K, V>.RangeView _v;
    public BPlusNavView(ConcurrentBTreeDictionary<K, V>.RangeView v) => _v = v;
    public V this[K key] { set => _v[key] = value; }
    public bool TryAdd(K key, V value) => _v.TryAdd(key, value);
    public bool Remove(K key) => _v.Remove(key);
    public bool ContainsKey(K key) => _v.ContainsKey(key);
    public int Count => _v.Count;
    public void Clear() => _v.Clear();
    public bool TryGetCeiling(K key, out KeyValuePair<K, V> e) => _v.TryGetCeiling(key, out e);
    public bool TryGetFloor(K key, out KeyValuePair<K, V> e) => _v.TryGetFloor(key, out e);
    public bool TryGetHigher(K key, out KeyValuePair<K, V> e) => _v.TryGetHigher(key, out e);
    public bool TryGetLower(K key, out KeyValuePair<K, V> e) => _v.TryGetLower(key, out e);
    public INavView<K, V> Reverse() => new BPlusNavView<K, V>(_v.Reverse());
    public IEnumerator<KeyValuePair<K, V>> GetEnumerator() => _v.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

// ---------------- B-link tree ----------------
internal sealed class BLinkNavMap<K, V> : INavMap<K, V> where K : notnull
{
    private readonly BLinkTree<K, V> _m;
    public BLinkNavMap(int order) => _m = new BLinkTree<K, V>(order);
    public V this[K key] { get => _m[key]; set => _m[key] = value; }
    public bool TryAdd(K key, V value) => _m.TryAdd(key, value);
    public bool TryRemove(K key, out V value) => _m.TryRemove(key, out value);
    public bool ContainsKey(K key) => _m.ContainsKey(key);
    public bool TryGetValue(K key, out V value) => _m.TryGetValue(key, out value);
    public int Count => _m.Count;
    public bool TryGetCeiling(K key, out KeyValuePair<K, V> e) => _m.TryGetCeiling(key, out e);
    public bool TryGetFloor(K key, out KeyValuePair<K, V> e) => _m.TryGetFloor(key, out e);
    public bool TryGetHigher(K key, out KeyValuePair<K, V> e) => _m.TryGetHigher(key, out e);
    public bool TryGetLower(K key, out KeyValuePair<K, V> e) => _m.TryGetLower(key, out e);
    public INavView<K, V> GetViewBetween(K from, K to) => new BLinkNavView<K, V>(_m.GetViewBetween(from, to));
    public INavView<K, V> GetViewBetween(K from, bool fi, K to, bool ti) => new BLinkNavView<K, V>(_m.GetViewBetween(from, fi, to, ti));
    public INavView<K, V> GetViewTo(K to, bool inc) => new BLinkNavView<K, V>(_m.GetViewTo(to, inc));
    public INavView<K, V> GetViewFrom(K from, bool inc) => new BLinkNavView<K, V>(_m.GetViewFrom(from, inc));
    public INavView<K, V> Reverse() => new BLinkNavView<K, V>(_m.Reverse());
    public void Validate() => _m.Validate();
    public IEnumerator<KeyValuePair<K, V>> GetEnumerator() => _m.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

internal sealed class BLinkNavView<K, V> : INavView<K, V> where K : notnull
{
    private readonly BLinkTree<K, V>.RangeView _v;
    public BLinkNavView(BLinkTree<K, V>.RangeView v) => _v = v;
    public V this[K key] { set => _v[key] = value; }
    public bool TryAdd(K key, V value) => _v.TryAdd(key, value);
    public bool Remove(K key) => _v.Remove(key);
    public bool ContainsKey(K key) => _v.ContainsKey(key);
    public int Count => _v.Count;
    public void Clear() => _v.Clear();
    public bool TryGetCeiling(K key, out KeyValuePair<K, V> e) => _v.TryGetCeiling(key, out e);
    public bool TryGetFloor(K key, out KeyValuePair<K, V> e) => _v.TryGetFloor(key, out e);
    public bool TryGetHigher(K key, out KeyValuePair<K, V> e) => _v.TryGetHigher(key, out e);
    public bool TryGetLower(K key, out KeyValuePair<K, V> e) => _v.TryGetLower(key, out e);
    public INavView<K, V> Reverse() => new BLinkNavView<K, V>(_v.Reverse());
    public IEnumerator<KeyValuePair<K, V>> GetEnumerator() => _v.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
