using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SharpArena.Allocators;

namespace MongoZen.Collections;

/// <summary>
/// Append-only open-addressed hash set backed by an <see cref="ArenaAllocator"/>.
/// </summary>
public unsafe struct ArenaHashSet<T>
    where T : unmanaged, IEquatable<T>
{
    private const float LoadFactorThreshold = 0.72f;
    private const int MinCapacity = 8;

    private T* _slots;
    private byte* _occupied;
    private int _capacityMask;
    private int _count;
    private ArenaAllocator _arena;

    public ArenaHashSet(ArenaAllocator arena, int initialCapacity = 128)
    {
        ArgumentNullException.ThrowIfNull(arena, nameof(arena));

        _arena = arena;
        _count = 0;

        int cap = NextPowerOfTwo(Math.Max(initialCapacity, MinCapacity));
        _slots = null;
        _occupied = null;
        _capacityMask = 0;
        AllocateArrays(cap);
    }

    public readonly int Count => _count;
    public readonly int Capacity => _capacityMask + 1;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Add(T item)
    {
        if (_count * 100 >= Capacity * 72)
            Grow();

        int mask  = _capacityMask;
        int slot  = item.GetHashCode() & mask;

        while (_occupied[slot] != 0)
        {
            if (_slots[slot].Equals(item))
                return false;

            slot = (slot + 1) & mask;
        }

        _slots[slot]    = item;
        _occupied[slot] = 1;
        _count++;
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly bool Contains(T item)
    {
        if (_count == 0) return false;

        int mask = _capacityMask;
        int slot = item.GetHashCode() & mask;

        while (_occupied[slot] != 0)
        {
            if (_slots[slot].Equals(item))
                return true;

            slot = (slot + 1) & mask;
        }

        return false;
    }

    public void Clear()
    {
        if (Capacity > 0)
        {
            NativeMemory.Clear(_occupied, (nuint)(Capacity * sizeof(byte)));
        }
        _count = 0;
    }

    public readonly Enumerator GetEnumerator() => new(_slots, _occupied, Capacity);

    public ref struct Enumerator
    {
        private readonly T* _slots;
        private readonly byte* _occupied;
        private readonly int _capacity;
        private int _index;

        internal Enumerator(T* slots, byte* occupied, int capacity)
        {
            _slots = slots;
            _occupied = occupied;
            _capacity = capacity;
            _index = -1;
        }

        public bool MoveNext()
        {
            while (++_index < _capacity)
            {
                if (_occupied[_index] != 0)
                    return true;
            }
            return false;
        }

        public readonly T Current
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _slots[_index];
        }
    }

    private void Grow()
    {
        int oldCap  = Capacity;
        int newCap  = oldCap * 2;

        T*    oldSlots    = _slots;
        byte* oldOccupied = _occupied;

        AllocateArrays(newCap);
        int newMask = _capacityMask;
        int newCount = 0;

        for (int i = 0; i < oldCap; i++)
        {
            if (oldOccupied[i] == 0) continue;

            T item = oldSlots[i];
            int slot = item.GetHashCode() & newMask;

            while (_occupied[slot] != 0)
                slot = (slot + 1) & newMask;

            _slots[slot]    = item;
            _occupied[slot] = 1;
            newCount++;
        }
        _count = newCount;
    }

    private void AllocateArrays(int capacity)
    {
        nuint slotBytes     = (nuint)(capacity * sizeof(T));
        nuint occupiedBytes = (nuint)(capacity * sizeof(byte));

        _slots    = (T*)    _arena.Alloc(slotBytes);
        _occupied = (byte*) _arena.Alloc(occupiedBytes);

        NativeMemory.Clear(_slots,    slotBytes);
        NativeMemory.Clear(_occupied, occupiedBytes);

        _capacityMask = capacity - 1;
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
