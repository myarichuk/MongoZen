using System;
using System.IO.Hashing;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using MongoDB.Bson;

namespace MongoZen;

/// <summary>
/// Discriminated-union identity fingerprint for any MongoDB document _id.
/// Always 20 bytes, blittable, stack-safe. 
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 20)]
public struct DocId : IEquatable<DocId>
{
    // Offset  0      : Kind (1 byte)
    // Offset  4..19  : identity payload (16 bytes)
    
    [FieldOffset(0)] public byte Kind;
    [FieldOffset(4)] private ulong _part1;
    [FieldOffset(12)] private ulong _part2;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Equals(DocId other)
    {
        return Kind == other.Kind && _part1 == other._part1 && _part2 == other._part2;
    }

    public override bool Equals(object? obj) => obj is DocId d && Equals(d);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public override int GetHashCode()
    {
        return HashCode.Combine(Kind, _part1, _part2);
    }

    public static bool operator ==(DocId a, DocId b) => a.Equals(b);
    public static bool operator !=(DocId a, DocId b) => !a.Equals(b);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void WriteHash128(ReadOnlySpan<byte> data)
    {
        var hash = XxHash128.HashToUInt128(data);
        unsafe
        {
            *(UInt128*)Unsafe.AsPointer(ref _part1) = hash;
        }
    }

    public static DocId FromObjectId(ObjectId oid)
    {
        var id = default(DocId);
        id.Kind = 0;
        var oidSpan = MemoryMarshal.CreateReadOnlySpan(ref oid, 1);
        var oidBytes = MemoryMarshal.AsBytes(oidSpan);

        unsafe
        {
            var dst = (byte*)Unsafe.AsPointer(ref id._part1);
            fixed (byte* src = oidBytes)
            {
                *(long*)dst = *(long*)src;           
                *(int*)(dst + 8) = *(int*)(src + 8);
            }
        }
        return id;
    }

    public static DocId FromGuid(Guid g)
    {
        var id = default(DocId);
        id.Kind = 1;
        unsafe
        {
            *(Guid*)Unsafe.AsPointer(ref id._part1) = g;
        }
        return id;
    }

    public static DocId FromInt32(int value)
    {
        var id = default(DocId);
        id.Kind = 2;
        id._part1 = (ulong)value;
        return id;
    }

    public static DocId FromInt64(long value)
    {
        var id = default(DocId);
        id.Kind = 3;
        id._part1 = (ulong)value;
        return id;
    }

    public static DocId FromString(string s)
    {
        var id = default(DocId);
        id.Kind = 4;
        id.WriteHash128(MemoryMarshal.AsBytes(s.AsSpan()));
        return id;
    }

    public static DocId FromHashable(IDocIdHashable hashable)
    {
        var id = default(DocId);
        id.Kind = 5;
        Span<byte> buf = stackalloc byte[256];
        int written = hashable.WriteIdBytes(buf);
        id.WriteHash128(buf[..written]);
        return id;
    }

    public static DocId FromBson(object obj)
    {
        var id = default(DocId);
        id.Kind = 6;
        var bytes = obj.ToBson(obj.GetType());
        id.WriteHash128(bytes);
        return id;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static DocId From(object? rawId)
    {
        ArgumentNullException.ThrowIfNull(rawId);

        return rawId switch
        {
            ObjectId oid         => FromObjectId(oid),
            Guid g               => FromGuid(g),
            int i                => FromInt32(i),
            long l               => FromInt64(l),
            string s             => FromString(s),
            IDocIdHashable h     => FromHashable(h),
            _                    => FromBson(rawId)
        };
    }
}
