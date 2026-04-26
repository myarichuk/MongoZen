using System;
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
    [FieldOffset(0)] public byte Kind;
    // offsets 1-3 are reserved/padding
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

    public static DocId FromObjectId(ObjectId oid)
    {
        var id = default(DocId);
        id.Kind = 0;
        // ObjectId is 12 bytes. We copy it into our 16-byte buffer.
        unsafe 
        {
            var p = (byte*)Unsafe.AsPointer(ref id._part1);
            Unsafe.WriteUnaligned(p, oid);
        }
        return id;
    }

    public static DocId FromGuid(Guid g)
    {
        var id = default(DocId);
        id.Kind = 1;
        unsafe 
        {
            var p = (Guid*)Unsafe.AsPointer(ref id._part1);
            *p = g;
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
        // 64-bit FNV-1a hash
        ulong hash = 14695981039346656037UL;
        foreach (char c in s)
        {
            hash = (hash ^ c) * 1099511628211UL;
        }
        id._part1 = hash;
        return id;
    }

    public static DocId FromHashable(IDocIdHashable hashable)
    {
        var id = default(DocId);
        id.Kind = 5;
        Span<byte> buf = stackalloc byte[256];
        int written = hashable.WriteIdBytes(buf);
        
        ulong hash = 14695981039346656037UL;
        foreach (byte b in buf[..written])
        {
            hash = (hash ^ b) * 1099511628211UL;
        }
        id._part1 = hash;
        return id;
    }

    public static DocId FromBson(object obj)
    {
        var id = default(DocId);
        id.Kind = 6;
        var bytes = obj.ToBson(obj.GetType());
        
        ulong hash = 14695981039346656037UL;
        foreach (byte b in bytes)
        {
            hash = (hash ^ b) * 1099511628211UL;
        }
        id._part1 = hash;
        return id;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static DocId From(object? rawId)
    {
        if (rawId == null) return default;

        return rawId switch
        {
            ObjectId oid => FromObjectId(oid),
            Guid g => FromGuid(g),
            int i => FromInt32(i),
            long l => FromInt64(l),
            string s => FromString(s),
            IDocIdHashable h => FromHashable(h),
            _ => FromBson(rawId)
        };
    }
}
