using System;
using System.Runtime.CompilerServices;

namespace MongoZen;

/// <summary>
/// Provides safe wrapper methods for unsafe pointer operations, 
/// allowing consumer assemblies to use MongoZen without needing 
/// &lt;AllowUnsafeBlocks&gt;true&lt;/AllowUnsafeBlocks&gt;.
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
