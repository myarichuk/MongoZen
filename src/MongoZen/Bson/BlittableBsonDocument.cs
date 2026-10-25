using System.Runtime.InteropServices;
using SharpArena.Collections;
using SharpArena.Allocators;
using MongoDB.Bson;
using System.Runtime.CompilerServices;

namespace MongoZen.Bson;

[StructLayout(LayoutKind.Sequential)]
public readonly unsafe struct BlittableBsonDocument
{
    private readonly byte* _bsonBytes;
    private readonly int _length;
    private readonly ArenaDictionary<ArenaUtf8String, int> _index;

    public int Length => _length;

    internal BlittableBsonDocument(byte* bsonBytes, int length, ArenaDictionary<ArenaUtf8String, int> index)
    {
        _bsonBytes = bsonBytes;
        _length = length;
        _index = index;
    }

    public byte* Pointer => _bsonBytes;
    public bool IsDefault => _bsonBytes == null;

    public bool ContainsKey(ReadOnlySpan<char> name) => TryGetElementOffset(name, out _);

    /// <summary>Number of top-level elements.</summary>
    public int Count => _length == 0 ? 0 : _index.Count;

    public ReadOnlySpan<byte> AsReadOnlySpan() => new(_bsonBytes, _length);

    public KeysCollection Keys => new(_index);

    public readonly struct KeysCollection(ArenaDictionary<ArenaUtf8String, int> index) : IEnumerable<ArenaUtf8String>
    {
        public Enumerator GetEnumerator() => new(index);
        IEnumerator<ArenaUtf8String> IEnumerable<ArenaUtf8String>.GetEnumerator() => GetEnumerator();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();

        public struct Enumerator(ArenaDictionary<ArenaUtf8String, int> index) : IEnumerator<ArenaUtf8String>
        {
            private IEnumerator<ArenaUtf8String> _inner = index.Keys.GetEnumerator();
            public ArenaUtf8String Current => _inner.Current;
            object? System.Collections.IEnumerator.Current => Current;
            public bool MoveNext() => _inner.MoveNext();
            public void Reset() => _inner.Reset();
            public void Dispose() => _inner.Dispose();
        }
    }

    public IEnumerable<ArenaUtf8String> KeysEnumerable => _index.Keys;

    public bool TryGetElementOffset(ReadOnlySpan<char> name, out int offset)
    {
        if (_length == 0)
        {
            offset = 0;
            return false;
        }
        return _index.TryGetValue(name, out offset);
    }

    public bool TryGetElementOffset(ArenaUtf8String name, out int offset)
    {
        if (_length == 0)
        {
            offset = 0;
            return false;
        }
        return _index.TryGetValue(name, out offset);
    }

    /// <summary>Reads a BSON Binary element as a managed array.</summary>
    public byte[] GetBinary(ReadOnlySpan<char> name)
    {
        if (!TryGetElementOffset(name, out var offset))
        {
            throw new KeyNotFoundException($"Property '{name.ToString()}' not found.");
        }
        return GetBinary(offset);
    }

    public byte[] GetBinary(int offset)
    {
        var p = GetElementData(offset, out var type, out _);
        return BsonValueReader.ReadBinary(p, type);
    }

    public int GetInt32(ReadOnlySpan<char> name)
    {
        if (!TryGetElementOffset(name, out var offset))
        {
            throw new KeyNotFoundException($"Property '{name.ToString()}' not found.");
        }
        return GetInt32(offset);
    }

    public long GetInt64(ReadOnlySpan<char> name)
    {
        if (!TryGetElementOffset(name, out var offset))
        {
            throw new KeyNotFoundException($"Property '{name.ToString()}' not found.");
        }
        return GetInt64(offset);
    }

    public double GetDouble(ReadOnlySpan<char> name)
    {
        if (!TryGetElementOffset(name, out var offset))
        {
            throw new KeyNotFoundException($"Property '{name.ToString()}' not found.");
        }
        return GetDouble(offset);
    }

    public bool GetBoolean(ReadOnlySpan<char> name)
    {
        if (!TryGetElementOffset(name, out var offset))
        {
            throw new KeyNotFoundException($"Property '{name.ToString()}' not found.");
        }
        return GetBoolean(offset);
    }

    public ReadOnlySpan<byte> GetStringBytes(ReadOnlySpan<char> name)
    {
        if (!TryGetElementOffset(name, out var offset))
        {
            throw new KeyNotFoundException($"Property '{name.ToString()}' not found.");
        }
        return GetStringBytes(offset);
    }

    public ObjectId GetObjectId(ReadOnlySpan<char> name)
    {
        if (!TryGetElementOffset(name, out var offset))
        {
            throw new KeyNotFoundException($"Property '{name.ToString()}' not found.");
        }
        return GetObjectId(offset);
    }

    public DateTime GetDateTime(ReadOnlySpan<char> name)
    {
        if (!TryGetElementOffset(name, out var offset))
        {
            throw new KeyNotFoundException($"Property '{name.ToString()}' not found.");
        }
        return GetDateTime(offset);
    }

    public Guid GetGuid(ReadOnlySpan<char> name)
    {
        if (!TryGetElementOffset(name, out var offset))
        {
            throw new KeyNotFoundException($"Property '{name.ToString()}' not found.");
        }
        return GetGuid(offset);
    }

    public decimal GetDecimal128(ReadOnlySpan<char> name)
    {
        if (!TryGetElementOffset(name, out var offset))
        {
            throw new KeyNotFoundException($"Property '{name.ToString()}' not found.");
        }
        return GetDecimal128(offset);
    }

    public BlittableBsonDocument GetDocument(ReadOnlySpan<char> name, ArenaAllocator arena)
    {
        if (!TryGetElementOffset(name, out var offset))
        {
            throw new KeyNotFoundException($"Property '{name.ToString()}' not found.");
        }
        return GetDocument(offset, arena);
    }

    public BlittableBsonDocument GetDocument(ArenaUtf8String name, ArenaAllocator arena)
    {
        if (!_index.TryGetValue(name, out var offset))
        {
            throw new KeyNotFoundException($"Property '{name}' not found.");
        }
        return GetDocument(offset, arena);
    }

    public T Get<T>(ArenaUtf8String name)
    {
        if (!_index.TryGetValue(name, out var offset))
        {
            throw new KeyNotFoundException($"Property '{name}' not found.");
        }
        return Get<T>(offset);
    }

    public BlittableBsonArray GetArray(ReadOnlySpan<char> name, ArenaAllocator arena)
    {
        if (!TryGetElementOffset(name, out var offset))
        {
            throw new KeyNotFoundException($"Property '{name.ToString()}' not found.");
        }
        return GetArray(offset, arena);
    }

    public string GetString(ReadOnlySpan<char> name)
    {
        var bytes = GetStringBytes(name);
        return System.Text.Encoding.UTF8.GetString(bytes);
    }

    public int GetInt32(int offset) => BsonValueReader.ReadInt32(GetElementData(offset, out var type, out _), type);

    public long GetInt64(int offset) => BsonValueReader.ReadInt64(GetElementData(offset, out var type, out _), type);

    public double GetDouble(int offset) => BsonValueReader.ReadDouble(GetElementData(offset, out var type, out _), type);

    public bool GetBoolean(int offset) => BsonValueReader.ReadBoolean(GetElementData(offset, out var type, out _), type);

    public ReadOnlySpan<byte> GetStringBytes(int offset) => BsonValueReader.ReadStringBytes(GetElementData(offset, out var type, out _), type);

    public string GetString(int offset) => BsonValueReader.ReadString(GetElementData(offset, out var type, out _), type);

    public ObjectId GetObjectId(int offset) => BsonValueReader.ReadObjectId(GetElementData(offset, out var type, out _), type);

    public DateTime GetDateTime(int offset) => BsonValueReader.ReadDateTime(GetElementData(offset, out var type, out _), type);

    public Guid GetGuid(int offset) => BsonValueReader.ReadGuid(GetElementData(offset, out var type, out _), type);

    public decimal GetDecimal128(int offset) => BsonValueReader.ReadDecimal128(GetElementData(offset, out var type, out _), type);

    public BlittableBsonDocument GetDocument(int offset, ArenaAllocator arena)
    {
        var p = GetElementData(offset, out var type, out var length);
        if (type != BlittableBsonConstants.BsonType.Document)
        {
            throw new InvalidCastException($"Cannot cast {type} to Document");
        }

        return ArenaBsonReader.ReadInPlace(p, length, arena);
    }

    public BlittableBsonArray GetArray(int offset, ArenaAllocator arena)
    {
        var p = GetElementData(offset, out var type, out var length);
        if (type != BlittableBsonConstants.BsonType.Array)
        {
            throw new InvalidCastException($"Cannot cast {type} to Array");
        }

        return new BlittableBsonArray(p, length, arena);
    }

    public T Get<T>(ReadOnlySpan<char> name)
    {
        if (!TryGetElementOffset(name, out var offset))
        {
            throw new KeyNotFoundException($"Property '{name.ToString()}' not found.");
        }
        return Get<T>(offset);
    }

    public T Get<T>(int offset)
    {
        if (typeof(T) == typeof(int))
        {
            return (T)(object)GetInt32(offset);
        }

        if (typeof(T) == typeof(long))
        {
            return (T)(object)GetInt64(offset);
        }

        if (typeof(T) == typeof(double))
        {
            return (T)(object)GetDouble(offset);
        }

        if (typeof(T) == typeof(bool))
        {
            return (T)(object)GetBoolean(offset);
        }

        if (typeof(T) == typeof(string))
        {
            return (T)(object)GetString(offset);
        }

        if (typeof(T) == typeof(ObjectId))
        {
            return (T)(object)GetObjectId(offset);
        }

        if (typeof(T) == typeof(DateTime))
        {
            return (T)(object)GetDateTime(offset);
        }

        if (typeof(T) == typeof(Guid))
        {
            return (T)(object)GetGuid(offset);
        }

        if (typeof(T) == typeof(decimal))
        {
            return (T)(object)GetDecimal128(offset);
        }

        var p = GetElementData(offset, out var type, out var length);
        return BlittableConverter<T>.Instance.Read(p, type, length);
    }

    /// <summary>
    /// Resolves an element offset (position of its type byte, as returned by <see cref="TryGetElementOffset(ReadOnlySpan{char}, out int)"/>)
    /// to a pointer at its data, with the element's type and data length.
    /// </summary>
    public byte* GetElementData(int offset, out BlittableBsonConstants.BsonType type, out int length)
    {
        type = (BlittableBsonConstants.BsonType)_bsonBytes[offset];
        var ptr = _bsonBytes + offset + 1;
        while (*ptr != 0) ptr++; 
        var dataPtr = ptr + 1;
        int dataPos = (int)(dataPtr - _bsonBytes);
        int endPos = ArenaBsonReader.SkipElement(_bsonBytes, dataPos, type);
        length = endPos - dataPos;
        return dataPtr;
    }

    /// <summary>Reads the element at <paramref name="offset"/> as <typeparamref name="T"/> through <see cref="ElementCodec{T}"/> (used by generated code).</summary>
    public T Read<T>(int offset, ArenaAllocator arena)
    {
        var p = GetElementData(offset, out var type, out var length);
        return ElementCodec<T>.ReadAt(p, type, length, arena);
    }

    /// <summary>Same as <see cref="GetElementData"/> for compiled expression trees, which cannot use pointer types.</summary>
    public nint GetElementDataPtr(int offset, out BlittableBsonConstants.BsonType type, out int length) =>
        (nint)GetElementData(offset, out type, out length);
}
