using System;
using Xunit;
using SharpArena.Allocators;
using MongoZen.Bson;
using MongoDB.Bson;

namespace MongoZen.Tests;

public class DynamicSerializerUnsupportedTypesTests
{
    public enum Status
    {
        Active,
        Inactive
    }

    public class PocoWithPrimitives
    {
        public Guid Guid { get; set; }
        public decimal Decimal { get; set; }
        public Status Status { get; set; }
    }

    [Fact]
    public void Dynamic_Serializer_Should_Handle_Guid_Decimal_And_Enums()
    {
        using var arena = new ArenaAllocator();
        var poco = new PocoWithPrimitives
        {
            Guid = Guid.NewGuid(),
            Decimal = 123.45m,
            Status = Status.Inactive
        };

        var writer = new ArenaBsonWriter(arena);
        DynamicBlittableSerializer<PocoWithPrimitives>.SerializeDelegate(ref writer, poco);
        var doc = writer.Commit(arena);

        var result = DynamicBlittableSerializer<PocoWithPrimitives>.DeserializeDelegate(doc, arena);

        Assert.Equal(poco.Guid, result.Guid);
        Assert.Equal(poco.Decimal, result.Decimal);
        Assert.Equal(poco.Status, result.Status);
    }
}
