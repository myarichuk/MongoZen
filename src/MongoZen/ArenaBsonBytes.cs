using System;

namespace MongoZen;

/// <summary>
/// A non-owning reference to BSON bytes stored in an Arena.
/// </summary>
public readonly unsafe struct ArenaBsonBytes
{
    public readonly byte* RawPtr;
    public readonly int Length;

    public ArenaBsonBytes(byte* ptr, int length)
    {
        RawPtr = ptr;
        Length = length;
    }

    public ReadOnlySpan<byte> AsReadOnlySpan() => RawPtr == null ? default : new ReadOnlySpan<byte>(RawPtr, Length);
}
