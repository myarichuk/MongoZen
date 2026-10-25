using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SharpArena.Allocators;
using SharpArena.Collections;
using MongoDB.Bson;

namespace MongoZen.Bson;

public readonly unsafe struct BlittableBsonArray : IReadOnlyList<BlittableBsonArray.Element>
{
    private readonly byte* _bsonBytes;
    private readonly int _length;
    private readonly ArenaAllocator _arena;
    private readonly ArenaList<int> _index;

    internal BlittableBsonArray(byte* bsonBytes, int length, ArenaAllocator arena)
    {
        _bsonBytes = bsonBytes;
        _length = length;
        _arena = arena;

        // Build index immediately using the contiguous ArenaList
        _index = new ArenaList<int>(arena, initialCapacity: 16);
        
        int pos = 4;
        while (pos < length - 1)
        {
            var type = (BlittableBsonConstants.BsonType)bsonBytes[pos];
            int nameStart = pos + 1;
            int nameEnd = nameStart;
            while (bsonBytes[nameEnd] != 0) nameEnd++;

            _index.Add(pos);

            var dataPtr = bsonBytes + nameEnd + 1;
            int dataPos = (int)(dataPtr - bsonBytes);
            pos = ArenaBsonReader.SkipElement(bsonBytes, dataPos, type);
        }
    }

    public int Count => _index.Length;

    public Element this[int index]
    {
        get
        {
            // ArenaList's indexer already does bounds checking and CheckAliveThrowIfNot
            int pos = _index[index];
            var type = (BlittableBsonConstants.BsonType)_bsonBytes[pos];
            
            // Skip type and name (e.g. "0", "1", "2")
            int nameStart = pos + 1;
            int nameEnd = nameStart;
            while (_bsonBytes[nameEnd] != 0) nameEnd++;

            var dataPtr = _bsonBytes + nameEnd + 1;
            int dataPos = (int)(dataPtr - _bsonBytes);
            int endPos = ArenaBsonReader.SkipElement(_bsonBytes, dataPos, type);

            return new Element(dataPtr, type, endPos - dataPos, _arena);
        }
    }

    public Enumerator GetEnumerator() => new(this);
    IEnumerator<Element> IEnumerable<Element>.GetEnumerator() => GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public struct Enumerator : IEnumerator<Element>
    {
        private readonly BlittableBsonArray _array;
        private int _index;
        private Element _current;

        internal Enumerator(BlittableBsonArray array)
        {
            _array = array;
            _index = -1;
            _current = default;
        }

        public bool MoveNext()
        {
            if (++_index >= _array.Count)
            {
                return false;
            }

            _current = _array[_index];
            return true;
        }

        public void Reset() => _index = -1;
        public Element Current => _current;
        object IEnumerator.Current => Current;
        public void Dispose() { }
    }

    public readonly struct Element
    {
        private readonly byte* _p;
        private readonly BlittableBsonConstants.BsonType _type;
        private readonly int _length;
        private readonly ArenaAllocator _arena;

        internal Element(byte* p, BlittableBsonConstants.BsonType type, int length, ArenaAllocator arena)
        {
            _p = p;
            _type = type;
            _length = length;
            _arena = arena;
        }

        public BlittableBsonConstants.BsonType Type => _type;

        /// <summary>Pointer at the element's data (after type byte and index name).</summary>
        public byte* Pointer => _p;

        /// <summary>Length of the element's data in bytes.</summary>
        public int Length => _length;

        public int GetInt32() => BsonValueReader.ReadInt32(_p, _type);

        public string GetString() => BsonValueReader.ReadString(_p, _type);

        public T Get<T>()
        {
            // Same coercion rules as BlittableBsonDocument's typed getters (Int32/Int64/Double interchangeable).
            if (typeof(T) == typeof(int)) return (T)(object)BsonValueReader.ReadInt32(_p, _type);
            if (typeof(T) == typeof(string)) return (T)(object)BsonValueReader.ReadString(_p, _type);
            if (typeof(T) == typeof(long)) return (T)(object)BsonValueReader.ReadInt64(_p, _type);
            if (typeof(T) == typeof(double)) return (T)(object)BsonValueReader.ReadDouble(_p, _type);
            if (typeof(T) == typeof(bool)) return (T)(object)BsonValueReader.ReadBoolean(_p, _type);
            if (typeof(T) == typeof(ObjectId)) return (T)(object)BsonValueReader.ReadObjectId(_p, _type);
            if (typeof(T) == typeof(DateTime)) return (T)(object)BsonValueReader.ReadDateTime(_p, _type);
            if (typeof(T) == typeof(Guid)) return (T)(object)BsonValueReader.ReadGuid(_p, _type);
            if (typeof(T) == typeof(decimal)) return (T)(object)BsonValueReader.ReadDecimal128(_p, _type);
            if (typeof(T) == typeof(byte[])) return (T)(object)BsonValueReader.ReadBinary(_p, _type);

            return BlittableConverter<T>.Instance.Read(_p, _type, _length);
        }

        public BlittableBsonDocument GetDocument()
        {
            if (_type != BlittableBsonConstants.BsonType.Document)
            {
                throw new InvalidCastException($"Cannot cast {_type} to Document");
            }

            return ArenaBsonReader.ReadInPlace(_p, _length, _arena);
        }
    }
}
