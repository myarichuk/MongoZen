using SharpArena.Allocators;
using SharpArena.Collections;

namespace MongoZen.Bson;

public static unsafe class ArenaBsonReader
{
    public static BlittableBsonDocument Read(byte[] bytes, ArenaAllocator arena)
    {
        return Read(new ReadOnlySpan<byte>(bytes), arena);
    }

    public static BlittableBsonDocument Read(ReadOnlySpan<byte> bytes, ArenaAllocator arena)
    {
        var len = bytes.Length;
        var pBuffer = (byte*)arena.Alloc((nuint)len);
        bytes.CopyTo(new Span<byte>(pBuffer, len));

        return ReadInPlace(pBuffer, len, arena);
    }

    public static BlittableBsonDocument ReadInPlace(byte* pBuffer, int len, ArenaAllocator arena)
    {
        var index = new ArenaDictionary<ArenaUtf8String, int>(arena);
        var keyCache = new ArenaDictionary<ArenaUtf8String, ArenaUtf8String>(arena);

        int pos = 4; // length header
        while (pos < len - 1)
        {
            var type = (BlittableBsonConstants.BsonType)pBuffer[pos];
            int nameStart = pos + 1;
            int nameEnd = nameStart;
            while (pBuffer[nameEnd] != 0) nameEnd++;

            var nameSpan = new ReadOnlySpan<byte>(pBuffer + nameStart, nameEnd - nameStart);
            
            // Revert to working path: Clone for now. 
            // Deduplication still works because keyCache stores the unique cloned instances.
            var tempName = ArenaUtf8String.Clone(nameSpan, arena);

            if (!keyCache.TryGetValue(tempName, out var name))
            {
                name = tempName;
                keyCache.Add(name, name);
            }

            index.Add(name, pos); // offset of the element (including type)

            pos = SkipElement(pBuffer, nameEnd + 1, type);
        }

        return new BlittableBsonDocument(pBuffer, len, index);
    }

    public static int SkipElement(byte* p, int dataPos, BlittableBsonConstants.BsonType type)
    {
        return type switch
        {
            BlittableBsonConstants.BsonType.Double => dataPos + 8,
            BlittableBsonConstants.BsonType.String => dataPos + 4 + *(int*)(p + dataPos),
            BlittableBsonConstants.BsonType.Document => dataPos + *(int*)(p + dataPos),
            BlittableBsonConstants.BsonType.Array => dataPos + *(int*)(p + dataPos),
            BlittableBsonConstants.BsonType.Binary => dataPos + 4 + 1 + *(int*)(p + dataPos),
            BlittableBsonConstants.BsonType.ObjectId => dataPos + 12,
            BlittableBsonConstants.BsonType.Boolean => dataPos + 1,
            BlittableBsonConstants.BsonType.DateTime => dataPos + 8,
            BlittableBsonConstants.BsonType.Null => dataPos,
            BlittableBsonConstants.BsonType.Int32 => dataPos + 4,
            BlittableBsonConstants.BsonType.Int64 => dataPos + 8,
            _ => throw new NotSupportedException($"BSON type {type} not yet supported in scanner")
        };
    }
}
