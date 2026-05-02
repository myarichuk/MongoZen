using MongoDB.Bson.Serialization;
using SharpArena.Allocators;
using MongoZen.Bson;

namespace MongoZen;

/// <summary>
/// A generic BSON serializer that captures the raw bytes into an arena during deserialization.
/// </summary>
public unsafe class ArenaEntitySerializer<T>(ArenaAllocator arena) : IBsonSerializer<T>
{
    public Type ValueType => typeof(T);

    public T Deserialize(BsonDeserializationContext context, BsonDeserializationArgs args)
    {
        var reader = context.Reader;
        using var rawBytes = reader.ReadRawBsonDocument();
        
        var len = rawBytes.Length;
        var pBuffer = (byte*)arena.Alloc((nuint)len);
        
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
            for (int i = 0; i < len; i++)
            {
                pBuffer[i] = rawBytes.GetByte(i);
            }
        }

        var doc = ArenaBsonReader.ReadInPlace(pBuffer, len, arena);
        var entity = DynamicBlittableSerializer<T>.DeserializeDelegate(doc, arena);
        
        // Note: The capture part is handled by the Session/ChangeTracker calling Track
        // But here we return the entity.
        return entity;
    }

    public void Serialize(BsonSerializationContext context, BsonSerializationArgs args, T value)
    {
        // Use DynamicBlittableSerializer to write to the writer
        var writer = context.Writer;
        var arenaWriter = new ArenaBsonWriter(arena);
        DynamicBlittableSerializer<T>.SerializeDelegate(ref arenaWriter, value);
        
        var doc = arenaWriter.Commit(arena);
        using var buffer = ArenaByteBuffer.Rent(doc.Pointer, doc.Length);
        writer.WriteRawBsonDocument(buffer);
    }

    object IBsonSerializer.Deserialize(BsonDeserializationContext context, BsonDeserializationArgs args) => 
        Deserialize(context, args);

    public void Serialize(BsonSerializationContext context, BsonSerializationArgs args, object value) => 
        Serialize(context, args, (T)value);
}
