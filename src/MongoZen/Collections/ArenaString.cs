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

        return new ReadOnlySpan<byte>(_ptr, _length).SequenceEqual(new ReadOnlySpan<byte>(other._ptr, other._length));
    }

    public override bool Equals(object? obj) => obj is ArenaString other && Equals(other);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Equals(string? other)
    {
        if (other == null) return _ptr == null;
        if (_ptr == null) return false;
        
        int otherByteCount = System.Text.Encoding.UTF8.GetByteCount(other);
        if (_length != otherByteCount) return false;

        var span = new ReadOnlySpan<byte>(_ptr, _length);
        
        if (otherByteCount <= 512)
        {
            byte* buffer = stackalloc byte[512];
            int written = System.Text.Encoding.UTF8.GetBytes(other, new Span<byte>(buffer, 512));
            return span.SequenceEqual(new ReadOnlySpan<byte>(buffer, written));
        }
        
        byte[] rented = System.Buffers.ArrayPool<byte>.Shared.Rent(otherByteCount);
        try
        {
            int written = System.Text.Encoding.UTF8.GetBytes(other, rented);
            return span.SequenceEqual(new ReadOnlySpan<byte>(rented, 0, written));
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(rented);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetHashCode(string? s)
    {
        if (s == null) return 0;
        int byteCount = System.Text.Encoding.UTF8.GetByteCount(s);
        
        if (byteCount <= 512)
        {
            byte* buffer = stackalloc byte[512];
            int written = System.Text.Encoding.UTF8.GetBytes(s, new Span<byte>(buffer, 512));
            var hash = System.IO.Hashing.XxHash3.HashToUInt64(new ReadOnlySpan<byte>(buffer, written));
            return (int)hash ^ (int)(hash >> 32);
        }
        
        byte[] rented = System.Buffers.ArrayPool<byte>.Shared.Rent(byteCount);
        try
        {
            int written = System.Text.Encoding.UTF8.GetBytes(s, rented);
            var hash = System.IO.Hashing.XxHash3.HashToUInt64(new ReadOnlySpan<byte>(rented, 0, written));
            return (int)hash ^ (int)(hash >> 32);
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(rented);
        }
    }

    public override int GetHashCode()
    {
        if (_ptr == null) return 0;
        var hash = System.IO.Hashing.XxHash3.HashToUInt64(new ReadOnlySpan<byte>(_ptr, _length));
        return (int)hash ^ (int)(hash >> 32);
    }

    public override string ToString()
    {
        if (_ptr == null) return string.Empty;
        return System.Text.Encoding.UTF8.GetString(_ptr, _length);
    }

    public static bool operator ==(ArenaString left, ArenaString right) => left.Equals(right);
    public static bool operator !=(ArenaString left, ArenaString right) => !left.Equals(right);
}

public static class ArenaDictionaryExtensions
{
    public static unsafe bool TryGetValue<TValue>(this ref ArenaDictionary<ArenaString, TValue> dict, string key, out TValue value)
        where TValue : unmanaged
    {
        if (dict.Count == 0)
        {
            value = default;
            return false;
        }

        int mask = dict.Capacity - 1;
        int slot = ArenaString.GetHashCode(key) & mask;

        // We need to access private fields of ArenaDictionary. 
        // Since they are internal, we can do it if we are in the same assembly.
        // Wait, ArenaDictionary fields are private.
        // I'll make them internal or use a trick.
        // Actually, I'll just update ArenaDictionary.cs to have this method.
        return dict.TryGetValue(key, out value);
    }
}
