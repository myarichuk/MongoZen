using System;
using Xunit;
using SharpArena.Allocators;
using MongoDB.Bson;
using MongoZen.Bson;

namespace MongoZen.Tests;

public unsafe class ArenaBsonWriterTests
{
    [Fact]
    public void Can_Write_Simple_Document()
    {
        using var arena = new ArenaAllocator();
        var writer = new ArenaBsonWriter(arena);

        writer.WriteStartDocument();
        writer.WriteInt32("a", 1);
        writer.WriteString("b", "hello");
        writer.WriteEndDocument();

        var doc = writer.Commit(arena);

        Assert.Equal(1, doc.GetInt32("a"));
        Assert.Equal("hello", System.Text.Encoding.UTF8.GetString(doc.GetStringBytes("b")));
    }

    [Fact]
    public void Can_Write_Nested_Document()
    {
        using var arena = new ArenaAllocator();
        var writer = new ArenaBsonWriter(arena);

        writer.WriteStartDocument();
        writer.WriteStartDocument("nested");
        writer.WriteInt32("x", 42);
        writer.WriteEndDocument();
        writer.WriteEndDocument();

        var doc = writer.Commit(arena);
        var nested = doc.GetDocument("nested", arena);

        Assert.Equal(42, nested.GetInt32("x"));
    }

    [Fact]
    public void Can_Write_Array()
    {
        using var arena = new ArenaAllocator();
        var writer = new ArenaBsonWriter(arena);

        writer.WriteStartDocument();
        writer.WriteStartArray("arr");
        writer.WriteInt32(0, 10);
        writer.WriteInt32(1, 20);
        writer.WriteEndArray();
        writer.WriteEndDocument();

        var doc = writer.Commit(arena);
        var array = doc.GetArray("arr", arena);

        Assert.Equal(2, array.Count);
        Assert.Equal(10, array[0].GetInt32());
        Assert.Equal(20, array[1].GetInt32());
    }

    [Fact]
    public void Can_Write_Complex_Types()
    {
        using var arena = new ArenaAllocator();
        var writer = new ArenaBsonWriter(arena);
        var oid = ObjectId.GenerateNewId();
        var now = DateTime.UtcNow;
        // BSON precision
        now = new DateTime(now.Year, now.Month, now.Day, now.Hour, now.Minute, now.Second, now.Millisecond, DateTimeKind.Utc);

        writer.WriteStartDocument();
        writer.WriteObjectId("oid", oid);
        writer.WriteDateTime("dt", now);
        writer.WriteBoolean("b", true);
        writer.WriteNull("n");
        writer.WriteEndDocument();

        var doc = writer.Commit(arena);

        Assert.Equal(oid, doc.GetObjectId("oid"));
        Assert.Equal(now, doc.GetDateTime("dt"));
        Assert.True(doc.GetBoolean("b"));
        // Null test? We don't have GetNull yet, but we can check TryGetElementOffset
        Assert.True(doc.TryGetElementOffset("n", out _));
    }
}
