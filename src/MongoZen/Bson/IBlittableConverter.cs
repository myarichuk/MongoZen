using MongoDB.Bson.IO;
using MongoDB.Bson.Serialization;

namespace MongoZen.Bson;

public unsafe interface IBlittableConverter<T>
{
    T Read(byte* p, BlittableBsonConstants.BsonType type, int length);
}

public static class BlittableConverter<T>
{
    private static IBlittableConverter<T>? _instance;

    public static IBlittableConverter<T> Instance
    {
        get => _instance ??= new BsonSerializerBridge<T>();
        set => _instance = value;
    }

    public static void Register(IBlittableConverter<T> converter) => _instance = converter;
}

internal sealed unsafe class BsonSerializerBridge<T> : IBlittableConverter<T>
{
    private readonly IBsonSerializer<T> _serializer = BsonSerializer.LookupSerializer<T>();

    public T Read(byte* p, BlittableBsonConstants.BsonType type, int length)
    {
        // Wrap arena pointer in a rented buffer
        using var buffer = ArenaByteBuffer.Rent(p, length);
        
        // Bridge point: raw arena bytes -> IByteBuffer -> ByteBufferStream -> BsonBinaryReader -> Serializer
        using var stream = new ByteBufferStream(buffer);
        using var reader = new BsonBinaryReader(stream);
        
        var context = BsonDeserializationContext.CreateRoot(reader);
        return _serializer.Deserialize(context);
    }
}
