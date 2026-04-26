using System.Buffers;
using System.Collections;
using System.Runtime.CompilerServices;

namespace MongoZen.Collections;

/// <summary>
/// A zero-allocation hash set struct that rents its storage from <see cref="ArrayPool{T}.Shared"/>.
/// Uses open-addressing with linear probing. MUST be disposed.
/// </summary>
public struct PooledHashSet<T> : IDisposable, IEnumerable<T>
{
    private T[]? _slots;
    private byte[]? _occupied; // 0 = empty, 1 = occupied
    private int _count;
    private int _capacityMask;
    private readonly IEqualityComparer<T> _comparer;

    public PooledHashSet(int initialCapacity = 16, IEqualityComparer<T>? comparer = null)
    {
        int cap = NextPowerOfTwo(initialCapacity);
        _slots = ArrayPool<T>.Shared.Rent(cap);
        _occupied = ArrayPool<byte>.Shared.Rent(cap);
        Array.Clear(_occupied, 0, cap);
        
        _capacityMask = cap - 1;
        _count = 0;
        _comparer = comparer ?? EqualityComparer<T>.Default;
    }

    public int Count => _count;

    public bool Add(T item)
    {
        if (_slots == null) this = new PooledHashSet<T>(16, _comparer);

        var slots = _slots!;
        if (_count * 100 >= slots.Length * 70)
        {
            Grow();
            slots = _slots!;
        }

        int hashCode = _comparer.GetHashCode(item!);
        int slot = hashCode & _capacityMask;

        while (_occupied![slot] != 0)
        {
            if (_comparer.Equals(slots[slot], item))
                return false;
            slot = (slot + 1) & _capacityMask;
        }

        slots[slot] = item;
        _occupied[slot] = 1;
        _count++;
        return true;
    }

    public bool Contains(T item)
    {
        if (_count == 0 || _slots == null) return false;

        int hashCode = _comparer.GetHashCode(item!);
        int slot = hashCode & _capacityMask;

        while (_occupied![slot] != 0)
        {
            if (_comparer.Equals(_slots![slot], item))
                return true;
            slot = (slot + 1) & _capacityMask;
        }

        return false;
    }

    private void Grow()
    {
        var oldSlots = _slots!;
        var oldOccupied = _occupied!;
        int oldCap = oldSlots.Length;
        int newCap = oldCap * 2;

        _slots = ArrayPool<T>.Shared.Rent(newCap);
        _occupied = ArrayPool<byte>.Shared.Rent(newCap);
        Array.Clear(_occupied, 0, newCap);
        _capacityMask = newCap - 1;
        _count = 0;

        for (int i = 0; i < oldCap; i++)
        {
            if (oldOccupied[i] != 0)
            {
                Add(oldSlots[i]);
            }
        }

        ArrayPool<T>.Shared.Return(oldSlots, clearArray: RuntimeHelpers.IsReferenceOrContainsReferences<T>());
        ArrayPool<byte>.Shared.Return(oldOccupied);
    }

    public void Clear()
    {
        if (_slots != null)
        {
            if (RuntimeHelpers.IsReferenceOrContainsReferences<T>())
                Array.Clear(_slots, 0, _slots.Length);
            Array.Clear(_occupied!, 0, _occupied!.Length);
        }
        _count = 0;
    }

    public void Dispose()
    {
        if (_slots != null)
        {
            ArrayPool<T>.Shared.Return(_slots, clearArray: RuntimeHelpers.IsReferenceOrContainsReferences<T>());
            ArrayPool<byte>.Shared.Return(_occupied!);
            _slots = null;
            _occupied = null;
        }
        _count = 0;
    }

    public Enumerator GetEnumerator() => new(this);
    IEnumerator<T> IEnumerable<T>.GetEnumerator() => GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public struct Enumerator : IEnumerator<T>
    {
        private readonly PooledHashSet<T> _set;
        private int _index;

        internal Enumerator(PooledHashSet<T> set)
        {
            _set = set;
            _index = -1;
        }

        public bool MoveNext()
        {
            if (_set._slots == null) return false;
            while (++_index < _set._slots.Length)
            {
                if (_set._occupied![_index] != 0)
                    return true;
            }
            return false;
        }

        public T Current => _set._slots![_index];
        object? IEnumerator.Current => Current;
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
