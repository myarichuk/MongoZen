using SharpArena.Collections;
using MongoZen.Collections;
using SharpArena.Allocators;
using Xunit;
using System.Runtime.InteropServices;

namespace MongoZen.Tests;

public class ArenaStringTests
{
    [Fact]
    public void ArenaString_Equality_And_Hashing()
    {
        using var arena = new ArenaAllocator();
        var s1 = ArenaString.Clone("string-A", arena);
        var s2 = ArenaString.Clone("string-A", arena);
        var s3 = ArenaString.Clone("completely-different-string-B", arena);

        // Content-based hashing (not pointer-based)
        Assert.Equal(s1.GetHashCode(), s2.GetHashCode());
        Assert.True(s1.Equals(s2));
        Assert.True(s1 == s2);
        
        Assert.NotEqual(s1.GetHashCode(), s3.GetHashCode());
        Assert.False(s1.Equals(s3));
        Assert.True(s1 != s3);
    }

    [Fact]
    public void ArenaString_Equals_String_Avoids_Allocations()
    {
        using var arena = new ArenaAllocator();
        var s1 = ArenaString.Clone("hello", arena);

        Assert.True(s1.Equals("hello"));
        Assert.False(s1.Equals("world"));
    }

    [Fact]
    public void GetHashCode_Static_Matches_Instance()
    {
        var s = "consistent-hash";
        using var arena = new ArenaAllocator();
        var arenaString = ArenaString.Clone(s, arena);

        // SharpArena uses System.HashCode on UTF-16 bytes
        var hash = new HashCode();
        hash.Add(s.Length);
        hash.AddBytes(MemoryMarshal.AsBytes(s.AsSpan()));
        int expectedHash = hash.ToHashCode();

        Assert.Equal(expectedHash, arenaString.GetHashCode());
        Assert.Equal(ArenaExtensions.GetHashCode(s), arenaString.GetHashCode());
    }
}
