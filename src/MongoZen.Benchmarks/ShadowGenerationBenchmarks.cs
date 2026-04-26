using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using BenchmarkDotNet.Attributes;
using SharpArena.Allocators;

namespace MongoZen.Benchmarks;

[MemoryDiagnoser]
public class ShadowGenerationBenchmarks
{
    private readonly ArenaAllocator _arena = new();
    private SimpleEntity _simple = null!;
    private LargeEntity _large = null!;

    [GlobalSetup]
    public void Setup()
    {
        _simple = new SimpleEntity { Id = "1", Name = "Test", Age = 30, IsActive = true, Score = 95.5 };
        _large = new LargeEntity();
        _large.Id = "large-1";
        for (int i = 0; i < 100; i++) _large.Name = "Name " + i;
    }

    [GlobalCleanup]
    public void Cleanup() => _arena.Dispose();

    [IterationCleanup]
    public void IterationCleanup() => _arena.Reset();

    [Benchmark]
    public unsafe IntPtr Materialize_Simple()
    {
        var ptr = _arena.Alloc((nuint)Unsafe.SizeOf<SimpleEntity_Shadow>());
        ref var s = ref Unsafe.AsRef<SimpleEntity_Shadow>((void*)ptr);
        s.From(_simple, _arena);
        return (IntPtr)ptr;
    }

    [Benchmark]
    public unsafe bool DirtyCheck_Simple()
    {
        var ptr = _arena.Alloc((nuint)Unsafe.SizeOf<SimpleEntity_Shadow>());
        ref var s = ref Unsafe.AsRef<SimpleEntity_Shadow>((void*)ptr);
        s.From(_simple, _arena);
        return s.IsDirty(_simple);
    }

    [Benchmark]
    public unsafe IntPtr Materialize_Large()
    {
        var ptr = _arena.Alloc((nuint)Unsafe.SizeOf<LargeEntity_Shadow>());
        ref var s = ref Unsafe.AsRef<LargeEntity_Shadow>((void*)ptr);
        s.From(_large, _arena);
        return (IntPtr)ptr;
    }

    [Benchmark]
    public unsafe bool DirtyCheck_Large()
    {
        var ptr = _arena.Alloc((nuint)Unsafe.SizeOf<LargeEntity_Shadow>());
        ref var s = ref Unsafe.AsRef<LargeEntity_Shadow>((void*)ptr);
        s.From(_large, _arena);
        return s.IsDirty(_large);
    }

    #region Entities and Shadows
    public class SimpleEntity
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public int Age { get; set; }
        public bool IsActive { get; set; }
        public double Score { get; set; }
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SimpleEntity_Shadow
    {
        public SharpArena.Collections.ArenaString Id;
        public SharpArena.Collections.ArenaString Name;
        public int Age;
        public bool IsActive;
        public double Score;

        public void From(SimpleEntity source, ArenaAllocator arena)
        {
            Id = SharpArena.Collections.ArenaString.Clone(source.Id, arena);
            Name = SharpArena.Collections.ArenaString.Clone(source.Name, arena);
            Age = source.Age;
            IsActive = source.IsActive;
            Score = source.Score;
        }

        public bool IsDirty(SimpleEntity current)
        {
            if (!Id.Equals(current.Id)) return true;
            if (!Name.Equals(current.Name)) return true;
            if (Age != current.Age) return true;
            if (IsActive != current.IsActive) return true;
            if (Score != current.Score) return true;
            return false;
        }
    }

    public class LargeEntity
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        // Imagine 100 more... we'll just use a few but represent the size
        public long V1, V2, V3, V4, V5, V6, V7, V8, V9, V10;
        public long V11, V12, V13, V14, V15, V16, V17, V18, V19, V20;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct LargeEntity_Shadow
    {
        public SharpArena.Collections.ArenaString Id;
        public SharpArena.Collections.ArenaString Name;
        public long V1, V2, V3, V4, V5, V6, V7, V8, V9, V10;
        public long V11, V12, V13, V14, V15, V16, V17, V18, V19, V20;

        public void From(LargeEntity source, ArenaAllocator arena)
        {
            Id = SharpArena.Collections.ArenaString.Clone(source.Id, arena);
            Name = SharpArena.Collections.ArenaString.Clone(source.Name, arena);
            V1 = source.V1; V2 = source.V2; V3 = source.V3; V4 = source.V4; V5 = source.V5;
            V6 = source.V6; V7 = source.V7; V8 = source.V8; V9 = source.V9; V10 = source.V10;
            V11 = source.V11; V12 = source.V12; V13 = source.V13; V14 = source.V14; V15 = source.V15;
            V16 = source.V16; V17 = source.V17; V18 = source.V18; V19 = source.V19; V20 = source.V20;
        }

        public bool IsDirty(LargeEntity current)
        {
            if (!Id.Equals(current.Id)) return true;
            if (!Name.Equals(current.Name)) return true;
            if (V1 != current.V1 || V2 != current.V2 || V3 != current.V3 || V4 != current.V4 || V5 != current.V5) return true;
            return false;
        }
    }
    #endregion
}
