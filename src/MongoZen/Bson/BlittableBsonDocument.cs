using System.Runtime.InteropServices;
using SharpArena.Collections;
using SharpArena.Allocators;
using MongoDB.Bson;

namespace MongoZen.Bson;

[StructLayout(LayoutKind.Sequential)]
public readonly unsafe struct BlittableBsonDocument
{
    private readonly byte* _bsonBytes;
    private readonly int _length;
    private readonly ArenaDictionary<ArenaUtf8String, int> _index;

    public int Length => _length;
    internal byte* Pointer => _bsonBytes;

    internal BlittableBsonDocument(byte* bsonBytes, int length, ArenaDictionary<ArenaUtf8String, int> index)
    {
        _bsonBytes = bsonBytes;
        _length = length;
        _index = index;
    }

    public ReadOnlySpan<byte> AsReadOnlySpan() => new(_bsonBytes, _length);

    public bool TryGetElementOffset(ReadOnlySpan<char> name, out int offset)
    {
        if (_length == 0)
        {
            offset = 0;
            return false;
        }
        return _index.TryGetValue(name, out offset);
    }

    public int GetInt32(ReadOnlySpan<char> name)
    {
        if (!TryGetElementOffset(name, out var offset))
        {
            throw new KeyNotFoundException($"Property '{name.ToString()}' not found.");
        }

        var p = GetDataPointer(offset, out var type);
        return type switch
        {
            BlittableBsonConstants.BsonType.Int32 => *(int*)p,
            BlittableBsonConstants.BsonType.Int64 => (int)*(long*)p,
            BlittableBsonConstants.BsonType.Double => (int)*(double*)p,
            _ => throw new InvalidCastException($"Cannot cast {type} to Int32")
        };
    }

    public long GetInt64(ReadOnlySpan<char> name)
    {
        if (!TryGetElementOffset(name, out var offset))
        {
            throw new KeyNotFoundException($"Property '{name.ToString()}' not found.");
        }

        var p = GetDataPointer(offset, out var type);
        return type switch
        {
            BlittableBsonConstants.BsonType.Int64 => *(long*)p,
            BlittableBsonConstants.BsonType.Int32 => *(int*)p,
            BlittableBsonConstants.BsonType.Double => (long)*(double*)p,
            _ => throw new InvalidCastException($"Cannot cast {type} to Int64")
        };
    }

    public double GetDouble(ReadOnlySpan<char> name)
    {
        if (!TryGetElementOffset(name, out var offset))
        {
            throw new KeyNotFoundException($"Property '{name.ToString()}' not found.");
        }

        var p = GetDataPointer(offset, out var type);
        return type switch
        {
            BlittableBsonConstants.BsonType.Double => *(double*)p,
            BlittableBsonConstants.BsonType.Int32 => *(int*)p,
            BlittableBsonConstants.BsonType.Int64 => *(long*)p,
            _ => throw new InvalidCastException($"Cannot cast {type} to Double")
        };
    }

    public bool GetBoolean(ReadOnlySpan<char> name)
    {
        if (!TryGetElementOffset(name, out var offset))
        {
            throw new KeyNotFoundException($"Property '{name.ToString()}' not found.");
        }

        var p = GetDataPointer(offset, out var type);
        if (type != BlittableBsonConstants.BsonType.Boolean)
        {
            throw new InvalidCastException($"Cannot cast {type} to Boolean");
        }

        return *p != 0;
    }

    public ReadOnlySpan<byte> GetStringBytes(ReadOnlySpan<char> name)
    {
        if (!TryGetElementOffset(name, out var offset))
        {
            throw new KeyNotFoundException($"Property '{name.ToString()}' not found.");
        }

        var p = GetDataPointer(offset, out var type);
        if (type != BlittableBsonConstants.BsonType.String)
        {
            throw new InvalidCastException($"Cannot cast {type} to String");
        }

        int len = *(int*)p;
        // BSON string length includes the null terminator
        return new ReadOnlySpan<byte>(p + 4, len - 1);
    }

    public ObjectId GetObjectId(ReadOnlySpan<char> name)
    {
        if (!TryGetElementOffset(name, out var offset))
        {
            throw new KeyNotFoundException($"Property '{name.ToString()}' not found.");
        }

        var p = GetDataPointer(offset, out var type);
        if (type != BlittableBsonConstants.BsonType.ObjectId)
        {
            throw new InvalidCastException($"Cannot cast {type} to ObjectId");
        }
        
        // TODO: Make a PR to MongoDB driver for Span-based constructor
        var bytes = new byte[12];
        for (int i = 0; i < 12; i++) bytes[i] = p[i];
        return new ObjectId(bytes);
    }

    public DateTime GetDateTime(ReadOnlySpan<char> name)
    {
        if (!TryGetElementOffset(name, out var offset))
        {
            throw new KeyNotFoundException($"Property '{name.ToString()}' not found.");
        }

        var p = GetDataPointer(offset, out var type);
        if (type != BlittableBsonConstants.BsonType.DateTime)
        {
            throw new InvalidCastException($"Cannot cast {type} to DateTime");
        }

        return DateTime.UnixEpoch.AddMilliseconds(*(long*)p);
    }

    public BlittableBsonDocument GetDocument(ReadOnlySpan<char> name, ArenaAllocator arena)
    {
        if (!TryGetElementOffset(name, out var offset))
        {
            throw new KeyNotFoundException($"Property '{name.ToString()}' not found.");
        }

        var p = GetDataPointer(offset, out var type, out var length);
        if (type != BlittableBsonConstants.BsonType.Document)
        {
            throw new InvalidCastException($"Cannot cast {type} to Document");
        }

        return ArenaBsonReader.ReadInPlace(p, length, arena);
    }

    public BlittableBsonArray GetArray(ReadOnlySpan<char> name, ArenaAllocator arena)
    {
        if (!TryGetElementOffset(name, out var offset))
        {
            throw new KeyNotFoundException($"Property '{name.ToString()}' not found.");
        }

        var p = GetDataPointer(offset, out var type, out var length);
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

        var p = GetDataPointer(offset, out var type, out var length);
        return BlittableConverter<T>.Instance.Read(p, type, length);
    }

    private byte* GetDataPointer(int elementOffset, out BlittableBsonConstants.BsonType type)
    {
        return GetDataPointer(elementOffset, out type, out _);
    }

    private byte* GetDataPointer(int elementOffset, out BlittableBsonConstants.BsonType type, out int dataLength)
    {
        type = (BlittableBsonConstants.BsonType)_bsonBytes[elementOffset];
        var ptr = _bsonBytes + elementOffset + 1;
        while (*ptr != 0) ptr++; // skip field name (c string)
        var dataPtr = ptr + 1;
        int dataPos = (int)(dataPtr - _bsonBytes);
        int endPos = ArenaBsonReader.SkipElement(_bsonBytes, dataPos, type);
        dataLength = endPos - dataPos;
        return dataPtr;
    }
}


