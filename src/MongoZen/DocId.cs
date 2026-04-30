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
public readonly struct DocId : IEquatable<DocId>
{
    // Offset  0      : Kind (1 byte)
    // Offset  4..11  : Part1 (8 bytes)
    // Offset  12..19 : Part2 (8 bytes)
    
    [FieldOffset(0)] public readonly byte Kind;
    [FieldOffset(4)] private readonly ulong _part1;
    [FieldOffset(12)] private readonly ulong _part2;

    public DocId(byte kind, ulong part1, ulong part2)
    {
        // Zero-initialize padding to ensure deterministic memory representation
        // for high-perf collections that might use byte-wise comparison.
        this = default;
        Kind = kind;
        _part1 = part1;
        _part2 = part2;
    }

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

    public static bool operator ==(in DocId a, in DocId b) => a.Equals(b);
    public static bool operator !=(in DocId a, in DocId b) => !a.Equals(b);

    public static DocId FromObjectId(ObjectId oid)
    {
        var oidSpan = MemoryMarshal.CreateReadOnlySpan(ref oid, 1);
        var oidBytes = MemoryMarshal.AsBytes(oidSpan);

        ulong p1;
        ulong p2;
        unsafe
        {
            fixed (byte* src = oidBytes)
            {
                p1 = *(ulong*)src;           
                var low = *(uint*)(src + 8);
                p2 = low;
            }
        }
        return new DocId(0, p1, p2);
    }

    public static DocId FromGuid(Guid g)
    {
        unsafe
        {
            var ptr = (ulong*)&g;
            return new DocId(1, ptr[0], ptr[1]);
        }
    }

    public static DocId FromInt32(int value)
    {
        return new DocId(2, (ulong)(uint)value, 0);
    }

    public static DocId FromInt64(long value)
    {
        return new DocId(3, (ulong)value, 0);
    }

    public static DocId FromString(string s)
    {
        // Note: For strings, we store a hash. This means we cannot reconstruct the raw ID
        // from the DocId alone. This is acceptable for tracking but requires care in public APIs.
        var hash = XxHash128.HashToUInt128(MemoryMarshal.AsBytes(s.AsSpan()));
        return new DocId(4, (ulong)(hash & ulong.MaxValue), (ulong)(hash >> 64));
    }

    public static DocId FromHashable(IDocIdHashable hashable)
    {
        Span<byte> buf = stackalloc byte[256];
        int written = hashable.WriteIdBytes(buf);
        var hash = XxHash128.HashToUInt128(buf[..written]);
        return new DocId(5, (ulong)(hash & ulong.MaxValue), (ulong)(hash >> 64));
    }

    public static DocId FromBson(object obj)
    {
        var bytes = obj.ToBson(obj.GetType());
        var hash = XxHash128.HashToUInt128(bytes);
        return new DocId(6, (ulong)(hash & ulong.MaxValue), (ulong)(hash >> 64));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static DocId From(object? rawId)
    {
        if (rawId == null) return default;

        return rawId switch
        {
            ObjectId oid         => FromObjectId(oid),
            Guid g               => FromGuid(g),
            int i                => FromInt32(i),
            long l               => FromInt64(l),
            string s             => FromString(s),
            IDocIdHashable h     => FromHashable(h),
            DocId d              => d,
            _                    => FromBson(rawId)
        };
    }

    /// <summary>
    /// Attempts to convert the DocId back to its raw BsonValue representation.
    /// This only works for non-hashed types (ObjectId, Guid, Int32, Int64).
    /// </summary>
    public BsonValue? ToBsonValue()
    {
        return Kind switch
        {
            0 => ToObjectId(),
            1 => BsonValue.Create(ToGuid()),
            2 => (int)(uint)_part1,
            3 => (long)_part1,
            _ => null // Cannot reconstruct from hash
        };
    }

    private ObjectId ToObjectId()
    {
        Span<byte> bytes = stackalloc byte[12];
        unsafe
        {
            fixed (byte* dst = bytes)
            {
                *(ulong*)dst = _part1;
                *(uint*)(dst + 8) = (uint)_part2;
            }
        }
        return new ObjectId(bytes.ToArray());
    }

    private Guid ToGuid()
    {
        unsafe
        {
            var p1 = _part1;
            var p2 = _part2;
            var ptr = (byte*)&p1; // This is a bit hacky but works for blittable structs
            // Actually simpler:
            Span<ulong> parts = stackalloc ulong[2];
            parts[0] = _part1;
            parts[1] = _part2;
            return MemoryMarshal.Cast<ulong, Guid>(parts)[0];
        }
    }
}