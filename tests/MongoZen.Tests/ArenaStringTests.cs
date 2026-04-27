using MongoZen.Collections;
using SharpArena.Allocators;
using Xunit;

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

        var expectedBytes = System.Text.Encoding.UTF8.GetBytes(s);
        var expectedHash64 = System.IO.Hashing.XxHash3.HashToUInt64(expectedBytes);
        int expectedHash32 = (int)expectedHash64 ^ (int)(expectedHash64 >> 32);

        Assert.Equal(expectedHash32, arenaString.GetHashCode());
        Assert.Equal(ArenaString.GetHashCode(s), arenaString.GetHashCode());
    }
}
