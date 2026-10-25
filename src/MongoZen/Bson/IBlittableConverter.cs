using System;
using System.Linq;
using MongoDB.Bson.IO;
using MongoDB.Bson.Serialization;

namespace MongoZen.Bson;

/// <summary>
/// Value-level converter: reads from / writes to the element *data* only. <see cref="BsonType"/> is the
/// element type byte the writer must emit before <see cref="Write"/> (Document by default; a converter
/// that writes, say, a Binary payload must say so or the resulting BSON is corrupt).
/// </summary>
public unsafe interface IBlittableConverter<T>
{
    T Read(byte* p, BlittableBsonConstants.BsonType type, int length);
    void Write(ref ArenaBsonWriter writer, T value);
    BlittableBsonConstants.BsonType BsonType => BlittableBsonConstants.BsonType.Document;
}

/// <summary>
/// Registry for custom <see cref="IBlittableConverter{T}"/>s. A converter registered here is used by
/// <see cref="ElementCodec{T}"/> for every position of <typeparamref name="T"/>, provided it is
/// registered before the first serializer touching <typeparamref name="T"/> is compiled.
/// </summary>
public static class BlittableConverter<T>
{
    private static IBlittableConverter<T>? _instance;
    private static bool _hasCustom;

    public static IBlittableConverter<T> Instance
    {
        get => _instance ??= CreateDefault();
        set
        {
            _instance = value;
            _hasCustom = true;
        }
    }

    /// <summary>True once user code registered a converter for <typeparamref name="T"/>.</summary>
    public static bool HasCustom => _hasCustom;

    public static void Register(IBlittableConverter<T> converter) => Instance = converter;

    private static IBlittableConverter<T> CreateDefault()
    {
        return BsonTypeModel.Classify(typeof(T), out _) switch
        {
            BsonTypeKind.Document => new DynamicBlittableConverter<T>(),
            BsonTypeKind.DriverBridged => new BsonSerializerBridge<T>(),
            // Scalars and collections are handled by ElementCodec<T>; a converter for them only
            // exists if the user registered one.
            _ => new UnsupportedConverter<T>(),
        };
    }
}

internal sealed unsafe class UnsupportedConverter<T> : IBlittableConverter<T>
{
    public T Read(byte* p, BlittableBsonConstants.BsonType type, int length) => throw Error();
    public void Write(ref ArenaBsonWriter writer, T value) => throw Error();

    private static NotSupportedException Error() => new(
        $"No IBlittableConverter<{typeof(T).Name}> is registered and the type is not a document type. Use ElementCodec<{typeof(T).Name}> " +
        "for built-in scalars/collections, or register a converter via BlittableConverter<T>.Register.");
}

internal sealed unsafe class DynamicBlittableConverter<T> : IBlittableConverter<T>
{
    public T Read(byte* p, BlittableBsonConstants.BsonType type, int length)
    {
        // Deserialization requires an ArenaAllocator to build the index.
        // We'll handle this in a specialized session/store method.
        throw new NotSupportedException("Dynamic deserialization requires an ArenaAllocator. Use DynamicBlittableSerializer<T>.DeserializeDelegate.");
    }

    public void Write(ref ArenaBsonWriter writer, T value)
    {
        DynamicBlittableSerializer<T>.SerializeDelegate(ref writer, value);
    }
}

internal sealed unsafe class BsonSerializerBridge<T> : IBlittableConverter<T>
{
    private readonly IBsonSerializer<T> _serializer = BsonSerializer.LookupSerializer<T>();

    public T Read(byte* p, BlittableBsonConstants.BsonType type, int length)
    {
        // Wrap arena pointer in a rented buffer
        using var buffer = PooledByteBuffer.Rent(p, length);
        
        // Bridge point: raw arena bytes -> IByteBuffer -> ByteBufferStream -> BsonBinaryReader -> Serializer
        using var stream = new ByteBufferStream(buffer);
        using var reader = new BsonBinaryReader(stream);
        
        var context = BsonDeserializationContext.CreateRoot(reader);
        return _serializer.Deserialize(context);
    }

    public void Write(ref ArenaBsonWriter writer, T value)
    {
        // Tier 3: Use the official driver to serialize to a buffer, then copy to arena.
        // This is the "compatibility valve".
        using var ms = new System.IO.MemoryStream();
        using (var bsonWriter = new BsonBinaryWriter(ms))
        {
            var context = BsonSerializationContext.CreateRoot(bsonWriter);
            _serializer.Serialize(context, value);
        }

        var buffer = ms.ToArray(); // Use ToArray to get exactly the right length
        writer.WriteRaw(buffer);
    }
}
