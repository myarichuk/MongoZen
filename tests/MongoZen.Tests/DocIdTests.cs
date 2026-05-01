using System;
using MongoDB.Bson;
using Xunit;

namespace MongoZen.Tests;

public class DocIdTests
{
    [Fact]
    public void Should_Roundtrip_ObjectId()
    {
        var oid = ObjectId.GenerateNewId();
        var docId = DocId.From(oid);
        
        Assert.Equal(0, docId.Kind);
        Assert.Equal(oid, docId.ToBsonValue()?.AsObjectId);
    }

    [Fact]
    public void Should_Roundtrip_Guid()
    {
        var guid = Guid.NewGuid();
        var docId = DocId.From(guid);
        
        Assert.Equal(1, docId.Kind);
        Assert.Equal(guid, docId.ToBsonValue()?.AsGuid);
    }

    [Fact]
    public void Should_Roundtrip_Int32()
    {
        var val = 42;
        var docId = DocId.From(val);
        
        Assert.Equal(2, docId.Kind);
        Assert.Equal(val, docId.ToBsonValue()?.AsInt32);
    }

    [Fact]
    public void Should_Roundtrip_Int64()
    {
        var val = 123456789012345L;
        var docId = DocId.From(val);
        
        Assert.Equal(3, docId.Kind);
        Assert.Equal(val, docId.ToBsonValue()?.AsInt64);
    }

    [Fact]
    public void Should_Hash_String()
    {
        var s = "some-id";
        var docId = DocId.From(s);
        
        Assert.Equal(4, docId.Kind);
        // Strings cannot be roundtripped
        Assert.Null(docId.ToBsonValue());
        
        var docId2 = DocId.From(s);
        Assert.Equal(docId, docId2);
        
        var docId3 = DocId.From("other-id");
        Assert.NotEqual(docId, docId3);
    }

    [Fact]
    public void Should_Handle_Null()
    {
        var docId = DocId.From(null);
        Assert.Equal(default, docId);
        Assert.Equal(0, docId.Kind);
    }

    [Fact]
    public void Should_Be_Blittable_And_Fixed_Size()
    {
        // DocId is explicitly Size = 20
        Assert.Equal(20, System.Runtime.InteropServices.Marshal.SizeOf<DocId>());
    }
}
