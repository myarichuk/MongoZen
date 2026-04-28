using System.Buffers;
using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace MongoZen.Collections;

/// <summary>
/// A lean dictionary class that rents its storage from <see cref="ArrayPool{T}.Shared"/>.
/// Uses open-addressing with linear probing. MUST be disposed.
/// </summary>
public class PooledDictionary<TKey, TValue> : IDisposable, IEnumerable<KeyValuePair<TKey, TValue>>
{
    private struct Entry
    {
        public TKey Key;
        public TValue Value;
    }

    private Entry[]? _entries;
    private byte[]? _occupied;
    private int _count;
    private int _capacityMask;
    private readonly IEqualityComparer<TKey>? _comparer;

    public PooledDictionary() : this(16) { }

    public PooledDictionary(int initialCapacity, IEqualityComparer<TKey>? comparer = null)
    {
        int cap = NextPowerOfTwo(initialCapacity);
        _entries = ArrayPool<Entry>.Shared.Rent(cap);
        _occupied = ArrayPool<byte>.Shared.Rent(cap);
        Array.Clear(_occupied, 0, cap);

        _capacityMask = cap - 1;
        _count = 0;
        _comparer = comparer;
    }

    public PooledDictionary(IEqualityComparer<TKey> comparer)
    {
        _entries = null;
        _occupied = null;
        _count = 0;
        _capacityMask = 0;
        _comparer = comparer;
    }

    private IEqualityComparer<TKey> Comparer => _comparer ?? EqualityComparer<TKey>.Default;

    public int Count => _count;

    public TValue this[TKey key]
    {
        get
        {
            if (TryGetValue(key, out var value)) return value;
            throw new KeyNotFoundException();
        }
        set => AddOrUpdate(key, value);
    }

    public void AddOrUpdate(TKey key, TValue value)
    {
        if (_entries == null) Initialize(16);
        var entries = _entries!;
        int hashCode = Comparer.GetHashCode(key!);
        int slot = hashCode & _capacityMask;

        while (_occupied![slot] != 0)
        {
            if (Comparer.Equals(entries[slot].Key, key))
            {
                entries[slot].Value = value;
                return;
            }
            slot = (slot + 1) & _capacityMask;
        }

        // It's a new key, check if we need to grow
        if (_count * 100 >= entries.Length * 70)
        {
            Grow();
            entries = _entries!;
            _capacityMask = entries.Length - 1;
            slot = hashCode & _capacityMask;

            // Re-find the slot after grow
            while (_occupied![slot] != 0)
            {
                slot = (slot + 1) & _capacityMask;
            }
        }

        entries[slot] = new Entry { Key = key, Value = value };
        _occupied[slot] = 1;
        _count++;
    }

    private void Initialize(int capacity)
    {
        int cap = NextPowerOfTwo(capacity);
        _entries = ArrayPool<Entry>.Shared.Rent(cap);
        _occupied = ArrayPool<byte>.Shared.Rent(cap);
        Array.Clear(_occupied, 0, cap);
        _capacityMask = cap - 1;
        _count = 0;
    }

    public void UpdateAllValues(Func<TKey, TValue, TValue> transform)
    {
        if (_entries == null) return;
        for (int i = 0; i < _entries.Length; i++)
        {
            if (_occupied![i] != 0)
            {
                _entries[i].Value = transform(_entries[i].Key, _entries[i].Value);
            }
        }
    }

    public bool TryGetValue(TKey key, [MaybeNullWhen(false)] out TValue value)
    {
        if (_count == 0 || _entries == null)
        {
            value = default;
            return false;
        }

        int hashCode = Comparer.GetHashCode(key!);
        int slot = hashCode & _capacityMask;

        while (_occupied![slot] != 0)
        {
            if (Comparer.Equals(_entries![slot].Key, key))
            {
                value = _entries[slot].Value;
                return true;
            }
            slot = (slot + 1) & _capacityMask;
        }

        value = default;
        return false;
    }

    public bool ContainsKey(TKey key) => TryGetValue(key, out _);

    public IEnumerable<TValue> Values => this.Select(kvp => kvp.Value);

    public bool Remove(TKey key)
    {
        if (_count == 0 || _entries == null) return false;

        int hashCode = Comparer.GetHashCode(key!);
        int slot = hashCode & _capacityMask;

        while (_occupied![slot] != 0)
        {
            if (Comparer.Equals(_entries![slot].Key, key))
            {
                // Simple linear probing removal is tricky. 
                // We'll use the "re-hash subsequent" approach for correctness.
                _occupied[slot] = 0;
                _entries[slot] = default;
                _count--;

                // Re-hash everything in the same cluster
                int next = (slot + 1) & _capacityMask;
                while (_occupied[next] != 0)
                {
                    var entry = _entries[next];
                    _occupied[next] = 0;
                    _entries[next] = default;
                    _count--;
                    AddOrUpdate(entry.Key, entry.Value);
                    next = (next + 1) & _capacityMask;
                }

                return true;
            }
            slot = (slot + 1) & _capacityMask;
        }

        return false;
    }

    private void Grow()
    {
        var oldEntries = _entries!;
        var oldOccupied = _occupied!;
        int oldCap = oldEntries.Length;
        int newCap = oldCap * 2;

        _entries = ArrayPool<Entry>.Shared.Rent(newCap);
        _occupied = ArrayPool<byte>.Shared.Rent(newCap);
        Array.Clear(_occupied, 0, newCap);
        _capacityMask = newCap - 1;
        _count = 0;

        for (int i = 0; i < oldCap; i++)
        {
            if (oldOccupied[i] != 0)
            {
                AddOrUpdate(oldEntries[i].Key, oldEntries[i].Value);
            }
        }

        ArrayPool<Entry>.Shared.Return(oldEntries, clearArray: RuntimeHelpers.IsReferenceOrContainsReferences<Entry>());
        ArrayPool<byte>.Shared.Return(oldOccupied);
    }

    public void Clear()
    {
        if (_entries != null)
        {
            if (RuntimeHelpers.IsReferenceOrContainsReferences<Entry>())
                Array.Clear(_entries, 0, _entries.Length);
            Array.Clear(_occupied!, 0, _occupied!.Length);
        }
        _count = 0;
    }

    public void Dispose()
    {
        if (_entries != null)
        {
            ArrayPool<Entry>.Shared.Return(_entries, clearArray: RuntimeHelpers.IsReferenceOrContainsReferences<Entry>());
            ArrayPool<byte>.Shared.Return(_occupied!);
            _entries = null;
            _occupied = null;
        }
        _count = 0;
    }

    public Enumerator GetEnumerator() => new(this);
    IEnumerator<KeyValuePair<TKey, TValue>> IEnumerable<KeyValuePair<TKey, TValue>>.GetEnumerator() => GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public struct Enumerator : IEnumerator<KeyValuePair<TKey, TValue>>
    {
        private readonly PooledDictionary<TKey, TValue> _dict;
        private int _index;

        internal Enumerator(PooledDictionary<TKey, TValue> dict)
        {
            _dict = dict;
            _index = -1;
        }

        public bool MoveNext()
        {
            if (_dict._entries == null) return false;
            while (++_index < _dict._entries.Length)
            {
                if (_dict._occupied![_index] != 0)
                    return true;
            }
            return false;
        }

        public KeyValuePair<TKey, TValue> Current => new(_dict._entries![_index].Key, _dict._entries[_index].Value);
        object IEnumerator.Current => Current;
        public void Reset() => _index = -1;
        public void Dispose() { }
    }

    private static int NextPowerOfTwo(int n)
    {
        if (n <= 1) return 1;
        n--;
        n |= n >> 1; n |= n >> 2; n |= n >> 4; n |= n >> 8; n |= n >> 16;
        return n + 1;
    }
}
