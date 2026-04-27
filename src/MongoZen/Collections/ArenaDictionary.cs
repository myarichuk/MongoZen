using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SharpArena.Allocators;

namespace MongoZen.Collections;

/// <summary>
/// Append-only open-addressed dictionary backed by an <see cref="ArenaAllocator"/>.
/// Uses a separate linear item buffer and a disposable sparse index for zero-copy growth.
/// </summary>
public unsafe struct ArenaDictionary<TKey, TValue>
    where TKey : unmanaged, IEquatable<TKey>
    where TValue : unmanaged
{
    private const float LoadFactorThreshold = 0.72f;
    private const int MinCapacity = 8;

    [StructLayout(LayoutKind.Sequential)]
    internal struct Entry
    {
        public TKey Key;
        public TValue Value;
    }

    private Entry* _entries;    // Linear buffer of items (never moves)
    private int* _buckets;      // Sparse index (disposable/leaked on growth)
    private int _capacityMask;  // Mask for _buckets
    private int _count;         // Number of items in _entries
    private int _entriesCapacity; // Allocated size of _entries
    private ArenaAllocator _arena;

    public ArenaDictionary(ArenaAllocator arena, int initialCapacity = 128)
    {
        ArgumentNullException.ThrowIfNull(arena, nameof(arena));

        _arena = arena;
        _count = 0;

        int cap = NextPowerOfTwo(Math.Max(initialCapacity, MinCapacity));
        _capacityMask = cap - 1;
        
        _entriesCapacity = initialCapacity;
        _entries = (Entry*)_arena.Alloc((nuint)(_entriesCapacity * sizeof(Entry)));
        
        _buckets = (int*)_arena.Alloc((nuint)(cap * sizeof(int)));
        NativeMemory.Clear(_buckets, (nuint)(cap * sizeof(int)));
        // Use -1 to indicate empty bucket, but NativeMemory.Clear sets to 0. 
        // We'll use 1-based indexing for buckets: 0 = empty, i+1 = index in _entries.
    }

    public readonly int Count => _count;
    public readonly int Capacity => _capacityMask + 1;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void AddOrUpdate(TKey key, TValue value)
    {
        // 1. Check if key exists and update it
        int mask = _capacityMask;
        int hashCode = key.GetHashCode();
        int slot = hashCode & mask;

        while (true)
        {
            int entryIndexPlusOne = _buckets[slot];
            if (entryIndexPlusOne == 0) break; // Empty slot found

            int entryIndex = entryIndexPlusOne - 1;
            if (_entries[entryIndex].Key.Equals(key))
            {
                _entries[entryIndex].Value = value;
                return;
            }
            slot = (slot + 1) & mask;
        }

        // 2. Not found, add new entry
        if (_count * 100 >= Capacity * 72)
        {
            GrowIndex();
            mask = _capacityMask;
            slot = hashCode & mask;
            while (_buckets[slot] != 0)
                slot = (slot + 1) & mask;
        }

        if (_count >= _entriesCapacity)
        {
            GrowEntries();
        }

        int newIndex = _count++;
        _entries[newIndex].Key = key;
        _entries[newIndex].Value = value;
        _buckets[slot] = newIndex + 1;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly bool TryGetValue(TKey key, out TValue value)
    {
        if (_count == 0)
        {
            value = default;
            return false;
        }

        int mask = _capacityMask;
        int slot = key.GetHashCode() & mask;

        while (true)
        {
            int entryIndexPlusOne = _buckets[slot];
            if (entryIndexPlusOne == 0) break;

            int entryIndex = entryIndexPlusOne - 1;
            if (_entries[entryIndex].Key.Equals(key))
            {
                value = _entries[entryIndex].Value;
                return true;
            }
            slot = (slot + 1) & mask;
        }

        value = default;
        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly bool TryGetValue(string key, out TValue value)
    {
        if (_count == 0 || typeof(TKey) != typeof(ArenaString))
        {
            value = default;
            return false;
        }

        int mask = _capacityMask;
        int slot = ArenaString.GetHashCode(key) & mask;

        while (true)
        {
            int entryIndexPlusOne = _buckets[slot];
            if (entryIndexPlusOne == 0) break;

            int entryIndex = entryIndexPlusOne - 1;
            // Unsafe cast TKey to ArenaString to call Equals(string)
            ref ArenaString arenaKey = ref Unsafe.As<TKey, ArenaString>(ref _entries[entryIndex].Key);
            if (arenaKey.Equals(key))
            {
                value = _entries[entryIndex].Value;
                return true;
            }
            slot = (slot + 1) & mask;
        }

        value = default;
        return false;
    }

    public void Clear()
    {
        if (Capacity > 0)
        {
            NativeMemory.Clear(_buckets, (nuint)(Capacity * sizeof(int)));
        }
        _count = 0;
    }

    public readonly Enumerator GetEnumerator() => new(_entries, _count);

    public ref struct Enumerator
    {
        private readonly Entry* _entries;
        private readonly int _count;
        private int _index;

        internal Enumerator(Entry* entries, int count)
        {
            _entries = entries;
            _count = count;
            _index = -1;
        }

        public bool MoveNext()
        {
            return ++_index < _count;
        }

        public readonly TKey Key => _entries[_index].Key;
        public readonly TValue Value => _entries[_index].Value;
    }

    private void GrowIndex()
    {
        int oldCap = Capacity;
        int newCap = oldCap * 2;

        int* newBuckets = (int*)_arena.Alloc((nuint)(newCap * sizeof(int)));
        NativeMemory.Clear(newBuckets, (nuint)(newCap * sizeof(int)));
        
        int newMask = newCap - 1;

        for (int i = 0; i < _count; i++)
        {
            TKey key = _entries[i].Key;
            int slot = key.GetHashCode() & newMask;
            while (newBuckets[slot] != 0)
                slot = (slot + 1) & newMask;

            newBuckets[slot] = i + 1;
        }

        _buckets = newBuckets;
        _capacityMask = newMask;
    }

    private void GrowEntries()
    {
        int oldCap = _entriesCapacity;
        int newCap = oldCap * 2;

        Entry* newEntries = (Entry*)_arena.Alloc((nuint)(newCap * sizeof(Entry)));
        NativeMemory.Copy(_entries, newEntries, (nuint)(oldCap * sizeof(Entry)));

        _entries = newEntries;
        _entriesCapacity = newCap;
    }


    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int NextPowerOfTwo(int n)
    {
        if (n <= 1) return 1;
        n--;
        n |= n >> 1;
        n |= n >> 2;
        n |= n >> 4;
        n |= n >> 8;
        n |= n >> 16;
        return n + 1;
    }
}
