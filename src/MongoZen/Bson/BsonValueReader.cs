using MongoDB.Bson;

namespace MongoZen.Bson;

/// <summary>
/// Reads scalar BSON values from a raw element data pointer. This is the single implementation
/// behind <see cref="BlittableBsonDocument"/>'s typed getters, <see cref="BlittableBsonArray.Element"/>
/// and the compiled <see cref="ElementCodec{T}"/> readers, so documents, arrays and dictionary values
/// all coerce identically (Int32/Int64/Double are interchangeable on read, as with the official driver).
/// </summary>
public static unsafe class BsonValueReader
{
    public static int ReadInt32(byte* p, BlittableBsonConstants.BsonType type) => type switch
    {
        BlittableBsonConstants.BsonType.Int32 => *(int*)p,
        BlittableBsonConstants.BsonType.Int64 => checked((int)*(long*)p),
        BlittableBsonConstants.BsonType.Double => checked((int)*(double*)p),
        _ => throw new InvalidCastException($"Cannot cast {type} to Int32"),
    };

    public static long ReadInt64(byte* p, BlittableBsonConstants.BsonType type) => type switch
    {
        BlittableBsonConstants.BsonType.Int64 => *(long*)p,
        BlittableBsonConstants.BsonType.Int32 => *(int*)p,
        BlittableBsonConstants.BsonType.Double => checked((long)*(double*)p),
        _ => throw new InvalidCastException($"Cannot cast {type} to Int64"),
    };

    public static double ReadDouble(byte* p, BlittableBsonConstants.BsonType type) => type switch
    {
        BlittableBsonConstants.BsonType.Double => *(double*)p,
        BlittableBsonConstants.BsonType.Int32 => *(int*)p,
        BlittableBsonConstants.BsonType.Int64 => *(long*)p,
        _ => throw new InvalidCastException($"Cannot cast {type} to Double"),
    };

    public static bool ReadBoolean(byte* p, BlittableBsonConstants.BsonType type)
    {
        if (type != BlittableBsonConstants.BsonType.Boolean)
        {
            throw new InvalidCastException($"Cannot cast {type} to Boolean");
        }

        return *p != 0;
    }

    public static ReadOnlySpan<byte> ReadStringBytes(byte* p, BlittableBsonConstants.BsonType type)
    {
        if (type != BlittableBsonConstants.BsonType.String)
        {
            throw new InvalidCastException($"Cannot cast {type} to String");
        }

        int strLen = *(int*)p;
        return new ReadOnlySpan<byte>(p + 4, strLen - 1);
    }

    public static string ReadString(byte* p, BlittableBsonConstants.BsonType type) =>
        System.Text.Encoding.UTF8.GetString(ReadStringBytes(p, type));

    public static ObjectId ReadObjectId(byte* p, BlittableBsonConstants.BsonType type)
    {
        if (type != BlittableBsonConstants.BsonType.ObjectId)
        {
            throw new InvalidCastException($"Cannot cast {type} to ObjectId");
        }

        return new ObjectId(new ReadOnlySpan<byte>(p, 12).ToArray());
    }

    public static DateTime ReadDateTime(byte* p, BlittableBsonConstants.BsonType type)
    {
        if (type != BlittableBsonConstants.BsonType.DateTime)
        {
            throw new InvalidCastException($"Cannot cast {type} to DateTime");
        }

        return BsonDateTimeUtil.FromMillisecondsSinceEpoch(*(long*)p);
    }

    public static Guid ReadGuid(byte* p, BlittableBsonConstants.BsonType type)
    {
        if (type != BlittableBsonConstants.BsonType.Binary)
        {
            throw new InvalidCastException($"Cannot cast {type} to Guid");
        }

        int len = *(int*)p;
        byte subtype = p[4];
        if (len != 16)
        {
            throw new InvalidOperationException($"Invalid Guid length: {len}");
        }

        var span = new ReadOnlySpan<byte>(p + 5, 16);
        return subtype switch
        {
            4 => new Guid(span, bigEndian: true),
            3 => new Guid(span),
            _ => throw new InvalidOperationException($"Unsupported Guid subtype: {subtype}"),
        };
    }

    public static decimal ReadDecimal128(byte* p, BlittableBsonConstants.BsonType type)
    {
        if (type != BlittableBsonConstants.BsonType.Decimal128)
        {
            throw new InvalidCastException($"Cannot cast {type} to Decimal128");
        }

        long low = *(long*)p;
        long high = *(long*)(p + 8);
        return Decimal128.ToDecimal(Decimal128.FromIEEEBits((ulong)high, (ulong)low));
    }

    /// <summary>
    /// Reads a BSON Binary payload (any subtype) as a fresh managed array.
    /// </summary>
    public static byte[] ReadBinary(byte* p, BlittableBsonConstants.BsonType type)
    {
        if (type != BlittableBsonConstants.BsonType.Binary)
        {
            throw new InvalidCastException($"Cannot cast {type} to Binary");
        }

        int len = *(int*)p;
        byte subtype = p[4];
        if (subtype == 2)
        {
            // Legacy "old binary" subtype carries a second length prefix.
            int innerLen = *(int*)(p + 5);
            return new ReadOnlySpan<byte>(p + 9, innerLen).ToArray();
        }

        return new ReadOnlySpan<byte>(p + 5, len).ToArray();
    }

    // ---- nint entry points for compiled expression trees (no pointer types allowed there) ----
    public static int ReadInt32(nint p, BlittableBsonConstants.BsonType type) => ReadInt32((byte*)p, type);
    public static long ReadInt64(nint p, BlittableBsonConstants.BsonType type) => ReadInt64((byte*)p, type);
    public static double ReadDouble(nint p, BlittableBsonConstants.BsonType type) => ReadDouble((byte*)p, type);
    public static bool ReadBoolean(nint p, BlittableBsonConstants.BsonType type) => ReadBoolean((byte*)p, type);
    public static string ReadString(nint p, BlittableBsonConstants.BsonType type) => ReadString((byte*)p, type);
    public static ObjectId ReadObjectId(nint p, BlittableBsonConstants.BsonType type) => ReadObjectId((byte*)p, type);
    public static DateTime ReadDateTime(nint p, BlittableBsonConstants.BsonType type) => ReadDateTime((byte*)p, type);
    public static Guid ReadGuid(nint p, BlittableBsonConstants.BsonType type) => ReadGuid((byte*)p, type);
    public static decimal ReadDecimal128(nint p, BlittableBsonConstants.BsonType type) => ReadDecimal128((byte*)p, type);
    public static byte[] ReadBinary(nint p, BlittableBsonConstants.BsonType type) => ReadBinary((byte*)p, type);

    public static ReadOnlySpan<byte> ReadBinarySpan(byte* p, BlittableBsonConstants.BsonType type)
    {
        if (type != BlittableBsonConstants.BsonType.Binary)
        {
            throw new InvalidCastException($"Cannot cast {type} to Binary");
        }

        int len = *(int*)p;
        byte subtype = p[4];
        if (subtype == 2)
        {
            int innerLen = *(int*)(p + 5);
            return new ReadOnlySpan<byte>(p + 9, innerLen);
        }

        return new ReadOnlySpan<byte>(p + 5, len);
    }
}

/// <summary>
/// BSON DateTime is milliseconds since the Unix epoch; .NET DateTime has 100ns ticks. Everything that
/// writes or compares DateTimes goes through here so the write side and the change detector agree on
/// the precision — otherwise a <c>DateTime.UtcNow</c> stored once is reported dirty on every save.
/// </summary>
public static class BsonDateTimeUtil
{
    private static readonly long UnixEpochTicks = DateTime.UnixEpoch.Ticks;

    public static long ToMillisecondsSinceEpoch(DateTime value)
    {
        // Integer tick arithmetic, not double TotalMilliseconds: exact for every representable date.
        // Truncation toward zero matches the official driver (BsonUtils.ToMillisecondsSinceEpoch).
        return (ToUniversal(value).Ticks - UnixEpochTicks) / TimeSpan.TicksPerMillisecond;
    }

    public static DateTime FromMillisecondsSinceEpoch(long ms) =>
        new(UnixEpochTicks + ms * TimeSpan.TicksPerMillisecond, DateTimeKind.Utc);

    /// <summary>Equality at BSON precision: two DateTimes that serialize to the same BSON value are equal.</summary>
    public static bool Equal(DateTime a, DateTime b) => ToMillisecondsSinceEpoch(a) == ToMillisecondsSinceEpoch(b);

    public static bool Equal(DateTime? a, DateTime? b) =>
        a.HasValue == b.HasValue && (!a.HasValue || Equal(a.GetValueOrDefault(), b.GetValueOrDefault()));

    private static DateTime ToUniversal(DateTime value)
    {
        if (value.Kind == DateTimeKind.Utc)
        {
            return value;
        }

        // Min/Max must not shift by the local offset (the driver special-cases these as well).
        if (value == DateTime.MinValue || value == DateTime.MaxValue)
        {
            return DateTime.SpecifyKind(value, DateTimeKind.Utc);
        }

        return value.ToUniversalTime();
    }
}
