using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SharpArena.Allocators;

namespace MongoZen.Collections;

/// <summary>
/// Append-only open-addressed dictionary backed by an <see cref="ArenaAllocator"/>.
/// </summary>
public unsafe struct ArenaDictionary<TKey, TValue>
    where TKey : unmanaged, IEquatable<TKey>
    where TValue : unmanaged
{
    private const float LoadFactorThreshold = 0.72f;
    private const int MinCapacity = 8;

    [StructLayout(LayoutKind.Sequential)]
    private struct Entry
    {
        public TKey Key;
        public TValue Value;
    }

    private Entry* _entries;
    private byte* _occupied;
    private int _capacityMask;
    private int _count;
    private ArenaAllocator _arena;

    public ArenaDictionary(ArenaAllocator arena, int initialCapacity = 128)
    {
        ArgumentNullException.ThrowIfNull(arena, nameof(arena));

        _arena = arena;
        _count = 0;

        int cap = NextPowerOfTwo(Math.Max(initialCapacity, MinCapacity));
        _entries = null;
        _occupied = null;
        _capacityMask = 0;
        AllocateArrays(cap);
    }

    public readonly int Count => _count;
    public readonly int Capacity => _capacityMask + 1;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void AddOrUpdate(TKey key, TValue value)
    {
        if (_count * 100 >= Capacity * 72)
            Grow();

        int mask = _capacityMask;
        int slot = key.GetHashCode() & mask;

        while (_occupied[slot] != 0)
        {
            if (_entries[slot].Key.Equals(key))
            {
                _entries[slot].Value = value;
                return;
            }
            slot = (slot + 1) & mask;
        }

        _entries[slot].Key = key;
        _entries[slot].Value = value;
        _occupied[slot] = 1;
        _count++;
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

        while (_occupied[slot] != 0)
        {
            if (_entries[slot].Key.Equals(key))
            {
                value = _entries[slot].Value;
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
            NativeMemory.Clear(_occupied, (nuint)(Capacity * sizeof(byte)));
        }
        _count = 0;
    }

    public readonly Enumerator GetEnumerator() => new(this);

    public ref struct Enumerator
    {
        private readonly ArenaDictionary<TKey, TValue> _dict;
        private int _index;

        internal Enumerator(in ArenaDictionary<TKey, TValue> dict)
        {
            _dict = dict;
            _index = -1;
        }

        public bool MoveNext()
        {
            int cap = _dict.Capacity;
            while (++_index < cap)
            {
                if (_dict._occupied[_index] != 0)
                    return true;
            }
            return false;
        }

        public readonly TKey Key => _dict._entries[_index].Key;
        public readonly TValue Value => _dict._entries[_index].Value;
    }

    private void Grow()
    {
        int oldCap = Capacity;
        int newCap = oldCap * 2;

        Entry* oldEntries = _entries;
        byte* oldOccupied = _occupied;

        AllocateArrays(newCap);
        int newMask = _capacityMask;
        int newCount = 0;

        for (int i = 0; i < oldCap; i++)
        {
            if (oldOccupied[i] == 0) continue;

            TKey key = oldEntries[i].Key;
            TValue val = oldEntries[i].Value;

            int slot = key.GetHashCode() & newMask;
            while (_occupied[slot] != 0)
                slot = (slot + 1) & newMask;

            _entries[slot].Key = key;
            _entries[slot].Value = val;
            _occupied[slot] = 1;
            newCount++;
        }
        _count = newCount;
    }

    private void AllocateArrays(int capacity)
    {
        nuint entryBytes = (nuint)(capacity * sizeof(Entry));
        nuint occupiedBytes = (nuint)(capacity * sizeof(byte));

        _entries = (Entry*)_arena.Alloc(entryBytes);
        _occupied = (byte*)_arena.Alloc(occupiedBytes);

        NativeMemory.Clear(_entries, entryBytes);
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
