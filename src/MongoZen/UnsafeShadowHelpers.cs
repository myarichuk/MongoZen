using System;
using System.Runtime.CompilerServices;

namespace MongoZen;

/// <summary>
/// A wrapper around an unmanaged pointer that tracks the arena generation in DEBUG builds.
/// </summary>
public readonly struct ShadowPtr
{
    public readonly IntPtr Pointer;
#if DEBUG
    public readonly int Generation;

    public ShadowPtr(IntPtr ptr, int generation)
    {
        Pointer = ptr;
        Generation = generation;
    }
#else
    public ShadowPtr(IntPtr ptr)
    {
        Pointer = ptr;
    }

    public ShadowPtr(IntPtr ptr, int generation) : this(ptr) { }
#endif

    public static implicit operator IntPtr(ShadowPtr shadow) => shadow.Pointer;
    public bool IsZero => Pointer == IntPtr.Zero;
    public static ShadowPtr Zero => new ShadowPtr(IntPtr.Zero, 0);
}

/// <summary>
/// Provides safe wrapper methods for unsafe pointer operations, 
/// allowing consumer assemblies to use MongoZen without needing 
/// <AllowUnsafeBlocks>true</AllowUnsafeBlocks>.
/// </summary>
public static class UnsafeShadowHelpers
{
    /// <summary>
    /// Gets a managed reference to a struct from an unmanaged pointer.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static unsafe ref T GetRef<T>(IntPtr ptr) where T : struct
    {
        return ref Unsafe.AsRef<T>((void*)ptr);
    }

    /// <summary>
    /// Sets a value at an unmanaged pointer.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static unsafe void SetRef<T>(IntPtr ptr, T value) where T : struct
    {
        Unsafe.AsRef<T>((void*)ptr) = value;
    }
}
