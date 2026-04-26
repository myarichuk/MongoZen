using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SharpArena.Allocators;

namespace MongoZen.Collections;

/// <summary>
/// Append-only open-addressed hash set backed by an <see cref="ArenaAllocator"/>.
/// Uses a separate linear item buffer and a disposable sparse index for zero-copy growth.
/// </summary>
public unsafe struct ArenaHashSet<T>
    where T : unmanaged, IEquatable<T>
{
    private const float LoadFactorThreshold = 0.72f;
    private const int MinCapacity = 8;

    private T* _items;          // Linear buffer of items (never moves)
    private int* _buckets;      // Sparse index (disposable/leaked on growth)
    private int _capacityMask;  // Mask for _buckets
    private int _count;         // Number of items in _items
    private int _itemsCapacity; // Allocated size of _items
    private ArenaAllocator _arena;

    public ArenaHashSet(ArenaAllocator arena, int initialCapacity = 128)
    {
        ArgumentNullException.ThrowIfNull(arena, nameof(arena));

        _arena = arena;
        _count = 0;

        int cap = NextPowerOfTwo(Math.Max(initialCapacity, MinCapacity));
        _capacityMask = cap - 1;
        
        _itemsCapacity = initialCapacity;
        _items = (T*)_arena.Alloc((nuint)(_itemsCapacity * sizeof(T)));
        
        _buckets = (int*)_arena.Alloc((nuint)(cap * sizeof(int)));
        NativeMemory.Clear(_buckets, (nuint)(cap * sizeof(int)));
        // Use 1-based indexing for buckets: 0 = empty, i+1 = index in _items.
    }

    public readonly int Count => _count;
    public readonly int Capacity => _capacityMask + 1;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Add(T item)
    {
        // 1. Check if item exists
        int mask = _capacityMask;
        int hashCode = item.GetHashCode();
        int slot = hashCode & mask;

        while (true)
        {
            int itemIndexPlusOne = _buckets[slot];
            if (itemIndexPlusOne == 0) break; // Empty slot found

            int itemIndex = itemIndexPlusOne - 1;
            if (_items[itemIndex].Equals(item))
            {
                return false;
            }
            slot = (slot + 1) & mask;
        }

        // 2. Not found, add new item
        if (_count * 100 >= Capacity * 72)
        {
            GrowIndex();
            mask = _capacityMask;
            slot = hashCode & mask;
            while (_buckets[slot] != 0)
                slot = (slot + 1) & mask;
        }

        if (_count >= _itemsCapacity)
        {
            GrowItems();
        }

        int newIndex = _count++;
        _items[newIndex] = item;
        _buckets[slot] = newIndex + 1;
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly bool Contains(T item)
    {
        if (_count == 0) return false;

        int mask = _capacityMask;
        int slot = item.GetHashCode() & mask;

        while (true)
        {
            int itemIndexPlusOne = _buckets[slot];
            if (itemIndexPlusOne == 0) return false;

            int itemIndex = itemIndexPlusOne - 1;
            if (_items[itemIndex].Equals(item))
            {
                return true;
            }
            slot = (slot + 1) & mask;
        }
    }

    public void Clear()
    {
        if (Capacity > 0)
        {
            NativeMemory.Clear(_buckets, (nuint)(Capacity * sizeof(int)));
        }
        _count = 0;
    }

    public readonly Enumerator GetEnumerator() => new(_items, _count);

    public ref struct Enumerator
    {
        private readonly T* _items;
        private readonly int _count;
        private int _index;

        internal Enumerator(T* items, int count)
        {
            _items = items;
            _count = count;
            _index = -1;
        }

        public bool MoveNext()
        {
            return ++_index < _count;
        }

        public readonly T Current
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _items[_index];
        }
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
            T item = _items[i];
            int slot = item.GetHashCode() & newMask;
            while (newBuckets[slot] != 0)
                slot = (slot + 1) & newMask;

            newBuckets[slot] = i + 1;
        }

        _buckets = newBuckets;
        _capacityMask = newMask;
    }

    private void GrowItems()
    {
        int oldCap = _itemsCapacity;
        int newCap = oldCap * 2;

        T* newItems = (T*)_arena.Alloc((nuint)(newCap * sizeof(T)));
        NativeMemory.Copy(_items, newItems, (nuint)(oldCap * sizeof(T)));

        _items = newItems;
        _itemsCapacity = newCap;
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
