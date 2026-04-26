using System.Runtime.InteropServices;
using SharpArena.Allocators;

namespace MongoZen;

public delegate void ShadowSetter<TShadow, TSource>(ref TShadow shadow, TSource source, ArenaAllocator arena);

[StructLayout(LayoutKind.Sequential)]
public struct KeyValuePairShadow<TKeyShadow, TValueShadow> 
    where TKeyShadow : unmanaged 
    where TValueShadow : unmanaged
{
    public TKeyShadow Key;
    public TValueShadow Value;

    public void From<TKey, TValue>(
        KeyValuePair<TKey, TValue> source, 
        ArenaAllocator arena,
        ShadowSetter<TKeyShadow, TKey> keySetter,
        ShadowSetter<TValueShadow, TValue> valueSetter)
    {
        keySetter(ref Key, source.Key, arena);
        valueSetter(ref Value, source.Value, arena);
    }
}
