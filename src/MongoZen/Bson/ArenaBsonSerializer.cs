using System.Runtime.CompilerServices;
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
        
        var segment = rawBytes.AccessBackingBytes(0);
        if (segment.Array != null && segment.Count == len)
        {
            fixed (byte* pSource = segment.Array)
            {
                Unsafe.CopyBlock(pBuffer, pSource + segment.Offset, (uint)len);
            }
        }
        else
        {
            // iterate chunks
            int copied = 0;
            while (copied < len)
            {
                var chunk = rawBytes.AccessBackingBytes(copied);
                if (chunk.Array != null)
                {
                    fixed (byte* pSource = chunk.Array)
                    {
                        Unsafe.CopyBlock(pBuffer + copied, pSource + chunk.Offset, (uint)chunk.Count);
                    }
                }
                else
                {
                    for (int i = 0; i < chunk.Count; i++)
                    {
                        pBuffer[copied + i] = rawBytes.GetByte(copied + i);
                    }
                }
                copied += chunk.Count;
            }
        }

        return ArenaBsonReader.ReadInPlace(pBuffer, len, arena);
    }

    public void Serialize(BsonSerializationContext context, BsonSerializationArgs args, BlittableBsonDocument value)
    {
        var writer = context.Writer;
        using var buffer = PooledByteBuffer.Rent(value.Pointer, value.Length);
        writer.WriteRawBsonDocument(buffer);
    }

    object IBsonSerializer.Deserialize(BsonDeserializationContext context, BsonDeserializationArgs args) => 
        Deserialize(context, args);

    public void Serialize(BsonSerializationContext context, BsonSerializationArgs args, object value) => 
        Serialize(context, args, (BlittableBsonDocument)value);
}
