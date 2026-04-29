using System;
using SharpArena.Allocators;
using SharpArena.Collections;
using MongoZen.Collections;
using Xunit;

namespace MongoZen.Tests;

public class ArenaCollectionTests
{
    [Fact]
    public void ArenaSet_BasicOperations()
    {
        using var arena = new ArenaAllocator();
        var set = new ArenaSet<int>(arena, 8);

        Assert.True(set.Add(1));
        Assert.True(set.Add(2));
        Assert.False(set.Add(1));
        Assert.Equal(2, set.Count);

        Assert.True(set.Contains(1));
        Assert.True(set.Contains(2));
        Assert.False(set.Contains(3));

        set.Clear();
        Assert.Equal(0, set.Count);
        Assert.False(set.Contains(1));
        
        Assert.True(set.Add(1));
        Assert.Equal(1, set.Count);
    }

    [Fact]
    public void ArenaSet_Growth()
    {
        using var arena = new ArenaAllocator();
        var set = new ArenaSet<int>(arena, 8);

        for (int i = 0; i < 100; i++)
        {
            set.Add(i);
        }

        Assert.Equal(100, set.Count);
        for (int i = 0; i < 100; i++)
        {
            Assert.True(set.Contains(i));
        }
    }

    [Fact]
    public void ArenaDictionary_BasicOperations()
    {
        using var arena = new ArenaAllocator();
        var dict = new ArenaDictionary<int, int>(arena, 8);

        dict.AddOrUpdate(1, 10);
        dict.AddOrUpdate(2, 20);
        dict.AddOrUpdate(1, 11);

        Assert.Equal(2, dict.Count);
        Assert.True(dict.TryGetValue(1, out int val1));
        Assert.Equal(11, val1);
        Assert.True(dict.TryGetValue(2, out int val2));
        Assert.Equal(20, val2);
        Assert.False(dict.TryGetValue(3, out _));

        dict.Clear();
        Assert.Equal(0, dict.Count);
        Assert.False(dict.TryGetValue(1, out _));
    }

    [Fact]
    public void ArenaDictionary_Growth()
    {
        using var arena = new ArenaAllocator();
        var dict = new ArenaDictionary<int, int>(arena, 8);

        for (int i = 0; i < 100; i++)
        {
            dict.AddOrUpdate(i, i * 10);
        }

        Assert.Equal(100, dict.Count);
        for (int i = 0; i < 100; i++)
        {
            Assert.True(dict.TryGetValue(i, out int val));
            Assert.Equal(i * 10, val);
        }
    }

    [Fact]
    public void ArenaSet_DocId_Support()
    {
        using var arena = new ArenaAllocator();
        var set = new ArenaSet<DocId>(arena, 8);

        var id1 = DocId.From("id-1");
        var id2 = DocId.From("id-2");

        Assert.True(set.Add(id1));
        Assert.True(set.Add(id2));
        Assert.False(set.Add(id1));
        Assert.Equal(2, set.Count);
        Assert.True(set.Contains(id1));
    }
}


