namespace MongoZen.Bson;

public unsafe class GuidConverter : IBlittableConverter<Guid>
{
    public static readonly GuidConverter Instance = new();

    public Guid Read(byte* p, BlittableBsonConstants.BsonType type, int length)
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

        if (subtype == 4) // Standard UUID (Big-endian parts)
        {
            return new Guid(new ReadOnlySpan<byte>(p + 5, 16), bigEndian: true);
        }
        
        if (subtype == 3) // Legacy .NET GUID
        {
            return new Guid(new ReadOnlySpan<byte>(p + 5, 16));
        }

        throw new InvalidOperationException($"Unsupported Guid subtype: {subtype}");
    }
}
