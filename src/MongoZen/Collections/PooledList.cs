using System.Buffers;
using System.Collections;
using System.Runtime.CompilerServices;

namespace MongoZen.Collections;

/// <summary>
/// A zero-allocation list class that rents its storage from <see cref="ArrayPool{T}.Shared"/>.
/// MUST be disposed to return the array to the pool.
/// </summary>
public class PooledList<T> : IDisposable, IEnumerable<T>
{
    private T[]? _array;
    private int _count;

    public PooledList() : this(16) { }

    public PooledList(int initialCapacity)
    {
        _array = ArrayPool<T>.Shared.Rent(initialCapacity);
        _count = 0;
    }

    public int Count => _count;

    public void Add(T item)
    {
        if (_array == null) _array = ArrayPool<T>.Shared.Rent(16);

        if (_count == _array.Length)
        {
            var newArray = ArrayPool<T>.Shared.Rent(_count * 2);
            Array.Copy(_array, newArray, _count);
            ArrayPool<T>.Shared.Return(_array, clearArray: RuntimeHelpers.IsReferenceOrContainsReferences<T>());
            _array = newArray;
        }

        _array[_count++] = item;
    }

    public void Clear()
    {
        if (RuntimeHelpers.IsReferenceOrContainsReferences<T>() && _array != null)
        {
            Array.Clear(_array, 0, _count);
        }
        _count = 0;
    }

    public T this[int index]
    {
        get => _array![index];
        set => _array![index] = value;
    }

    public void Dispose()
    {
        if (_array != null)
        {
            ArrayPool<T>.Shared.Return(_array, clearArray: RuntimeHelpers.IsReferenceOrContainsReferences<T>());
            _array = null;
        }
        _count = 0;
    }

    public Enumerator GetEnumerator() => new(this);

    IEnumerator<T> IEnumerable<T>.GetEnumerator() => GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public struct Enumerator : IEnumerator<T>
    {
        private readonly PooledList<T> _list;
        private int _index;

        internal Enumerator(PooledList<T> list)
        {
            _list = list;
            _index = -1;
        }

        public bool MoveNext() => ++_index < _list._count;

        public T Current => _list[_index];

        object? IEnumerator.Current => Current;

        public void Reset() => _index = -1;

        public void Dispose() { }
    }
}
