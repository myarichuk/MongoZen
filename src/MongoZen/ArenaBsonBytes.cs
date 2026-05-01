using System;
// ReSharper disable MemberCanBePrivate.Global

namespace MongoZen;

/// <summary>
/// A non-owning reference to BSON bytes stored in an Arena.
/// </summary>
public readonly unsafe struct ArenaBsonBytes(byte* ptr, int length)
{
    public readonly byte* RawPtr = ptr;
    public readonly int Length = length;

    public ReadOnlySpan<byte> AsReadOnlySpan() => 
        RawPtr == null ? default : new ReadOnlySpan<byte>(RawPtr, Length);
}
