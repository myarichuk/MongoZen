using System;
using System.Collections.Generic;
using MongoZen;
using MongoZen.Bson;
using MongoZen.ChangeTracking;
using SharpArena.Allocators;
using Xunit;
using MongoDB.Bson;
using MongoDB.Driver;

using MongoDB.Bson.Serialization;

namespace MongoZen.Tests;

[Document]
public partial class TypeSupportDoc
{
    public Guid GuidProp { get; set; }
    public decimal DecimalProp { get; set; }
    public TestEnum EnumProp { get; set; }
}

public enum TestEnum
{
    Value1,
    Value2
}

public class ShadowGeneratorTypeSupportTests
{
    [Fact]
    public void Shadow_Generator_Should_Handle_Guid_Decimal_And_Enums()
    {
        using var arena = new ArenaAllocator();
        var doc = new TypeSupportDoc
        {
            GuidProp = Guid.NewGuid(),
            DecimalProp = 123.456m,
            EnumProp = TestEnum.Value2
        };

        var writer = new ArenaBsonWriter(arena);
        TypeSupportDoc.Serialize(ref writer, doc);
        var blittable = writer.Commit(arena);

        var deserialized = TypeSupportDoc.Deserialize(blittable, arena);

        Assert.Equal(doc.GuidProp, deserialized.GuidProp);
        Assert.Equal(doc.DecimalProp, deserialized.DecimalProp);
        Assert.Equal(doc.EnumProp, deserialized.EnumProp);
    }

    [Fact]
    public void Shadow_Generator_Should_Diff_Guid_Decimal_And_Enums()
    {
        using var arena = new ArenaAllocator();
        var doc = new TypeSupportDoc
        {
            GuidProp = Guid.NewGuid(),
            DecimalProp = 123.456m,
            EnumProp = TestEnum.Value1
        };

        var writer = new ArenaBsonWriter(arena);
        TypeSupportDoc.Serialize(ref writer, doc);
        var snapshot = writer.Commit(arena);

        doc.EnumProp = TestEnum.Value2;
        doc.DecimalProp = 789.012m;

        var builder = new ArenaUpdateDefinitionBuilder(arena);
        TypeSupportDoc.BuildUpdate(doc, snapshot, ref builder, arena, default);

        Assert.True(builder.HasChanges);
        var update = builder.Build();
        
        var setDoc = update.GetDocument("$set".AsSpan(), arena);
        Assert.Equal((int)TestEnum.Value2, setDoc.GetInt32("EnumProp".AsSpan()));
        Assert.Equal(789.012m, setDoc.GetDecimal128("DecimalProp".AsSpan()));
        Assert.False(setDoc.ContainsKey("GuidProp".AsSpan()));
    }
}
