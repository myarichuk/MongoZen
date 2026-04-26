using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using SharpArena.Allocators;

namespace MongoZen.Collections;

/// <summary>
/// A blazingly fast, unmanaged string representation stored in an Arena.
/// Uses SIMD intrinsics for comparison and fast hashing.
/// </summary>
public readonly unsafe struct ArenaString : IEquatable<ArenaString>
{
    private readonly byte* _ptr;
    private readonly int _length;

    public int Length => _length;
    public byte* Pointer => _ptr;

    public ArenaString(byte* ptr, int length)
    {
        _ptr = ptr;
        _length = length;
    }

    public static ArenaString Clone(string? source, ArenaAllocator arena)
    {
        if (source == null) return default;
        
        int byteCount = System.Text.Encoding.UTF8.GetByteCount(source);
        byte* ptr = (byte*)arena.Alloc((nuint)byteCount);
        
        fixed (char* sPtr = source)
        {
            System.Text.Encoding.UTF8.GetBytes(sPtr, source.Length, ptr, byteCount);
        }
        
        return new ArenaString(ptr, byteCount);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Equals(ArenaString other)
    {
        if (_length != other._length) return false;
        if (_ptr == other._ptr) return true;
        if (_ptr == null || other._ptr == null) return false;

        return CompareMemory(_ptr, other._ptr, _length);
    }

    public override bool Equals(object? obj) => obj is ArenaString other && Equals(other);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Equals(string? other)
    {
        if (other == null) return _ptr == null;
        if (_ptr == null) return false;
        
        // Potential optimization: check length first if we can get UTF8 byte count cheaply,
        // but for simplicity and correctness we use Span.
        var span = new ReadOnlySpan<byte>(_ptr, _length);
        int otherByteCount = System.Text.Encoding.UTF8.GetByteCount(other);
        if (_length != otherByteCount) return false;

        // SequenceEqual is highly optimized and uses SIMD under the hood
        Span<byte> otherBytes = stackalloc byte[512];
        if (otherByteCount <= 512)
        {
            int written = System.Text.Encoding.UTF8.GetBytes(other, otherBytes);
            return span.SequenceEqual(otherBytes[..written]);
        }
        
        return span.SequenceEqual(System.Text.Encoding.UTF8.GetBytes(other));
    }

    public override int GetHashCode()
    {
        if (_ptr == null) return 0;
        
        // Use a fast non-cryptographic hash (XxHash3 is excellent for this)
        // System.IO.Hashing.XxHash3 is available in our project.
        var span = new ReadOnlySpan<byte>(_ptr, _length);
        var hash = System.IO.Hashing.XxHash3.HashToUInt64(span);
        return (int)hash ^ (int)(hash >> 32);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool CompareMemory(byte* a, byte* b, int length)
    {
        // Use SIMD intrinsics for comparison
        int offset = 0;

        if (Vector256.IsHardwareAccelerated && length >= Vector256<byte>.Count)
        {
            while (offset <= length - Vector256<byte>.Count)
            {
                if (Vector256.Load(a + offset) != Vector256.Load(b + offset))
                    return false;
                offset += Vector256<byte>.Count;
            }
        }

        if (Vector128.IsHardwareAccelerated && offset <= length - Vector128<byte>.Count)
        {
            while (offset <= length - Vector128<byte>.Count)
            {
                if (Vector128.Load(a + offset) != Vector128.Load(b + offset))
                    return false;
                offset += Vector128<byte>.Count;
            }
        }

        // Remaining bytes
        while (offset < length)
        {
            if (a[offset] != b[offset])
                return false;
            offset++;
        }

        return true;
    }

    public override string ToString()
    {
        if (_ptr == null) return string.Empty;
        return System.Text.Encoding.UTF8.GetString(_ptr, _length);
    }

    public static bool operator ==(ArenaString left, ArenaString right) => left.Equals(right);
    public static bool operator !=(ArenaString left, ArenaString right) => !left.Equals(right);
}
