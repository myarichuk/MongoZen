using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SharpArena.Collections;

namespace MongoZen.Collections;

public static unsafe class ArenaExtensions
{
    [StructLayout(LayoutKind.Sequential)]
    private struct ArenaStringProxy
    {
        public char* Ptr;
        public int Len;
        public int Gen;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool TryGetValue<TValue>(this ArenaDictionary<ArenaString, TValue> dict, string key, out TValue value) where TValue : unmanaged
    {
        if (key == null)
        {
            value = default!;
            return false;
        }

        fixed (char* ptr = key)
        {
            ArenaStringProxy proxy = new() { Ptr = ptr, Len = key.Length, Gen = 0 };
            return dict.TryGetValue(Unsafe.As<ArenaStringProxy, ArenaString>(ref proxy), out value);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void AddOrUpdate<TKey, TValue>(this ref ArenaDictionary<TKey, TValue> dict, TKey key, TValue value) 
        where TKey : unmanaged, IEquatable<TKey>
        where TValue : unmanaged
    {
        dict[key] = value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetHashCode(string? s)
    {
        if (s == null) return 0;
        var hash = new HashCode();
        hash.Add(s.Length);
        hash.AddBytes(MemoryMarshal.AsBytes(s.AsSpan()));
        return hash.ToHashCode();
    }
}
