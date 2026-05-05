using System;
using System.Linq;
using MongoDB.Bson.IO;
using MongoDB.Bson.Serialization;

namespace MongoZen.Bson;

public unsafe interface IBlittableConverter<T>
{
    T Read(byte* p, BlittableBsonConstants.BsonType type, int length);
    void Write(ref ArenaBsonWriter writer, T value);
}

public static class BlittableConverter<T>
{
    private static IBlittableConverter<T>? _instance;

    public static IBlittableConverter<T> Instance
    {
        get => _instance ??= CreateDefault();
        set => _instance = value;
    }

    public static void Register(IBlittableConverter<T> converter) => _instance = converter;

    private static IBlittableConverter<T> CreateDefault()
    {
        var type = typeof(T);
        if (type.IsClass || (type.IsValueType && !type.IsPrimitive && !type.IsEnum && type != typeof(decimal)))
        {
            // For complex types, use the dynamic serializer if it's not a known MongoDB type
            // AND it doesn't have MongoDB attributes (which require the official driver's logic)
            bool isMongoType = type.Namespace?.StartsWith("MongoDB.Bson") ?? false;
            if (!isMongoType && !HasBsonAttributes(type))
            {
                return new DynamicBlittableConverter<T>();
            }
        }

        return new BsonSerializerBridge<T>();
    }

    private static bool HasBsonAttributes(Type type)
    {
        if (System.Reflection.CustomAttributeExtensions.GetCustomAttributes(type, true).Any(a => a.GetType().Namespace?.StartsWith("MongoDB.Bson.Serialization.Attributes") ?? false))
        {
            return true;
        }

        foreach (var prop in type.GetProperties())
        {
            if (System.Reflection.CustomAttributeExtensions.GetCustomAttributes(prop, true).Any(a => a.GetType().Namespace?.StartsWith("MongoDB.Bson.Serialization.Attributes") ?? false))
            {
                return true;
            }
        }

        return false;
    }
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
