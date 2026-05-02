using MongoDB.Bson.Serialization;
using SharpArena.Allocators;

namespace MongoZen.Bson;

public unsafe class ArenaBsonSerializer(ArenaAllocator arena) : IBsonSerializer<BlittableBsonDocument>
{
    public Type ValueType => typeof(BlittableBsonDocument);

    public BlittableBsonDocument Deserialize(BsonDeserializationContext context, BsonDeserializationArgs args)
    {
        var reader = context.Reader;
        using var rawBytes = reader.ReadRawBsonDocument();
        
        var len = rawBytes.Length;
        var pBuffer = (byte*)arena.Alloc((nuint)len);
        
        // Copy directly from the driver's buffer to our arena
        // If the driver's buffer is contiguous, we can get a segment
        var segment = rawBytes.AccessBackingBytes(0);
        if (segment.Array != null)
        {
            fixed (byte* pSource = segment.Array)
            {
                System.Runtime.CompilerServices.Unsafe.CopyBlock(pBuffer, pSource + segment.Offset, (uint)len);
            }
        }
        else
        {
            // Fallback for non-contiguous or custom buffers
            // Since we want zero-allocation, we rent a small buffer for the copy if needed,
            // but for raw documents they are usually contiguous.
            for (int i = 0; i < len; i++)
            {
                pBuffer[i] = rawBytes.GetByte(i);
            }
        }

        return ArenaBsonReader.ReadInPlace(pBuffer, len, arena);
    }

    public void Serialize(BsonSerializationContext context, BsonSerializationArgs args, BlittableBsonDocument value)
    {
        var writer = context.Writer;
        using var buffer = ArenaByteBuffer.Rent(value.Pointer, value.Length);
        writer.WriteRawBsonDocument(buffer);
    }

    object IBsonSerializer.Deserialize(BsonDeserializationContext context, BsonDeserializationArgs args) => 
        Deserialize(context, args);

    public void Serialize(BsonSerializationContext context, BsonSerializationArgs args, object value) => 
        Serialize(context, args, (BlittableBsonDocument)value);
}
