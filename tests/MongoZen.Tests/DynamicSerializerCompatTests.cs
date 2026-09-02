using System;
using System.Collections.Generic;
using Xunit;
using SharpArena.Allocators;
using MongoZen.Bson;
using MongoZen.ChangeTracking;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace MongoZen.Tests;

public class DynamicSerializerCompatTests
{
    public class GetOnlyCollectionPoco
    {
        public string Name { get; set; } = "";
        public List<int> Items { get; } = [];
    }

    [Fact]
    public void Get_Only_Collection_Property_Is_Not_Serialized()
    {
        using var arena = new ArenaAllocator();
        var poco = new GetOnlyCollectionPoco { Name = "Test" };
        poco.Items.Add(1);
        poco.Items.Add(2);

        var writer = new ArenaBsonWriter(arena);
        DynamicBlittableSerializer<GetOnlyCollectionPoco>.SerializeDelegate(ref writer, poco);
        var doc = writer.Commit(arena);

        Assert.Equal("Test", doc.GetString("Name"));
        Assert.False(doc.ContainsKey("Items".AsSpan()));
    }

    public enum NumericStatus
    {
        Active,
        Inactive
    }

    public enum StringStatus
    {
        Active,
        Inactive
    }

    public class EnumRepresentationPoco
    {
        public NumericStatus Numeric { get; set; }

        [BsonRepresentation(BsonType.String)]
        public StringStatus StringAttributed { get; set; }
    }

    [Fact]
    public void Numeric_Enum_Property_Round_Trips_As_Int()
    {
        using var arena = new ArenaAllocator();
        var poco = new EnumRepresentationPoco { Numeric = NumericStatus.Inactive, StringAttributed = StringStatus.Active };

        var writer = new ArenaBsonWriter(arena);
        DynamicBlittableSerializer<EnumRepresentationPoco>.SerializeDelegate(ref writer, poco);
        var doc = writer.Commit(arena);

        Assert.Equal((int)NumericStatus.Inactive, doc.GetInt32("Numeric"));

        var result = DynamicBlittableSerializer<EnumRepresentationPoco>.DeserializeDelegate(doc, arena);
        Assert.Equal(NumericStatus.Inactive, result.Numeric);
    }

    [Fact]
    public void BsonRepresentation_String_Enum_Property_Round_Trips_As_String()
    {
        using var arena = new ArenaAllocator();
        var poco = new EnumRepresentationPoco { Numeric = NumericStatus.Active, StringAttributed = StringStatus.Inactive };

        var writer = new ArenaBsonWriter(arena);
        DynamicBlittableSerializer<EnumRepresentationPoco>.SerializeDelegate(ref writer, poco);
        var doc = writer.Commit(arena);

        Assert.Equal(nameof(StringStatus.Inactive), doc.GetString("StringAttributed"));

        var result = DynamicBlittableSerializer<EnumRepresentationPoco>.DeserializeDelegate(doc, arena);
        Assert.Equal(StringStatus.Inactive, result.StringAttributed);
    }

    [Fact]
    public void BsonRepresentation_String_Enum_Property_Diff_Detects_Change()
    {
        using var arena = new ArenaAllocator();
        var entity = new EnumRepresentationPoco { StringAttributed = StringStatus.Active };

        var writer = new ArenaBsonWriter(arena);
        DynamicBlittableSerializer<EnumRepresentationPoco>.SerializeDelegate(ref writer, entity);
        var snapshot = writer.Commit(arena);

        entity.StringAttributed = StringStatus.Inactive;
        var builder = new ArenaUpdateDefinitionBuilder(arena);
        DynamicBlittableSerializer<EnumRepresentationPoco>.BuildUpdateDelegate(entity, snapshot, ref builder, arena, default);

        Assert.True(builder.HasChanges);
        var doc = builder.Build();
        var setDoc = doc.GetDocument("$set".AsSpan(), arena);
        Assert.True(setDoc.ContainsKey("StringAttributed".AsSpan()));
    }

    [Fact]
    public void BsonRepresentation_String_Enum_Property_Reads_Legacy_Numeric_Value()
    {
        // Simulates a document written before this property was annotated with
        // [BsonRepresentation(BsonType.String)] (or written by other, numeric-writing code) - the
        // fast route must still be able to read it during a gradual migration.
        using var arena = new ArenaAllocator();
        var bsonDoc = new BsonDocument
        {
            { "Numeric", (int)NumericStatus.Active },
            { "StringAttributed", (int)StringStatus.Inactive }
        };
        var doc = ArenaBsonReader.Read(bsonDoc.ToBson(), arena);

        var result = DynamicBlittableSerializer<EnumRepresentationPoco>.DeserializeDelegate(doc, arena);

        Assert.Equal(StringStatus.Inactive, result.StringAttributed);
    }

    [Fact]
    public void BsonRepresentation_String_Enum_Property_Diff_Detects_Change_Against_Legacy_Numeric_Snapshot()
    {
        using var arena = new ArenaAllocator();
        var bsonDoc = new BsonDocument
        {
            { "Numeric", (int)NumericStatus.Active },
            { "StringAttributed", (int)StringStatus.Active }
        };
        var snapshot = ArenaBsonReader.Read(bsonDoc.ToBson(), arena);

        var entity = new EnumRepresentationPoco { Numeric = NumericStatus.Active, StringAttributed = StringStatus.Inactive };
        var builder = new ArenaUpdateDefinitionBuilder(arena);
        DynamicBlittableSerializer<EnumRepresentationPoco>.BuildUpdateDelegate(entity, snapshot, ref builder, arena, default);

        Assert.True(builder.HasChanges);
        var updateDoc = builder.Build();
        var setDoc = updateDoc.GetDocument("$set".AsSpan(), arena);
        Assert.Equal(nameof(StringStatus.Inactive), setDoc.GetString("StringAttributed"));
    }

    public class NullableEnumPoco
    {
        public NumericStatus? Numeric { get; set; }

        [BsonRepresentation(BsonType.String)]
        public StringStatus? StringAttributed { get; set; }
    }

    [Fact]
    public void Nullable_Enum_Properties_Round_Trip_With_Value()
    {
        using var arena = new ArenaAllocator();
        var poco = new NullableEnumPoco { Numeric = NumericStatus.Inactive, StringAttributed = StringStatus.Inactive };

        var writer = new ArenaBsonWriter(arena);
        DynamicBlittableSerializer<NullableEnumPoco>.SerializeDelegate(ref writer, poco);
        var doc = writer.Commit(arena);

        Assert.Equal((int)NumericStatus.Inactive, doc.GetInt32("Numeric"));
        Assert.Equal(nameof(StringStatus.Inactive), doc.GetString("StringAttributed"));

        var result = DynamicBlittableSerializer<NullableEnumPoco>.DeserializeDelegate(doc, arena);
        Assert.Equal(NumericStatus.Inactive, result.Numeric);
        Assert.Equal(StringStatus.Inactive, result.StringAttributed);
    }

    [Fact]
    public void Nullable_Enum_Properties_Round_Trip_With_Null()
    {
        using var arena = new ArenaAllocator();
        var poco = new NullableEnumPoco { Numeric = null, StringAttributed = null };

        var writer = new ArenaBsonWriter(arena);
        DynamicBlittableSerializer<NullableEnumPoco>.SerializeDelegate(ref writer, poco);
        var doc = writer.Commit(arena);

        var result = DynamicBlittableSerializer<NullableEnumPoco>.DeserializeDelegate(doc, arena);
        Assert.Null(result.Numeric);
        Assert.Null(result.StringAttributed);
    }

    [Fact]
    public void Nullable_Enum_Property_Diff_Detects_Change()
    {
        using var arena = new ArenaAllocator();
        var entity = new NullableEnumPoco { StringAttributed = StringStatus.Active };

        var writer = new ArenaBsonWriter(arena);
        DynamicBlittableSerializer<NullableEnumPoco>.SerializeDelegate(ref writer, entity);
        var snapshot = writer.Commit(arena);

        entity.StringAttributed = StringStatus.Inactive;
        var builder = new ArenaUpdateDefinitionBuilder(arena);
        DynamicBlittableSerializer<NullableEnumPoco>.BuildUpdateDelegate(entity, snapshot, ref builder, arena, default);

        Assert.True(builder.HasChanges);
        var doc = builder.Build();
        var setDoc = doc.GetDocument("$set".AsSpan(), arena);
        Assert.Equal(nameof(StringStatus.Inactive), setDoc.GetString("StringAttributed"));
    }

    [Fact]
    public void Nullable_Enum_Property_Diff_Detects_Null_To_Value_Transition()
    {
        // Regression test: the typed read used to compare against the snapshot value
        // (GetInt32/GetString) throws InvalidCastException when the snapshot element is BSON Null,
        // which is exactly what a null -> value transition looks like.
        using var arena = new ArenaAllocator();
        var entity = new NullableEnumPoco { Numeric = null, StringAttributed = null };

        var writer = new ArenaBsonWriter(arena);
        DynamicBlittableSerializer<NullableEnumPoco>.SerializeDelegate(ref writer, entity);
        var snapshot = writer.Commit(arena);

        entity.Numeric = NumericStatus.Inactive;
        entity.StringAttributed = StringStatus.Inactive;
        var builder = new ArenaUpdateDefinitionBuilder(arena);
        DynamicBlittableSerializer<NullableEnumPoco>.BuildUpdateDelegate(entity, snapshot, ref builder, arena, default);

        Assert.True(builder.HasChanges);
        var doc = builder.Build();
        var setDoc = doc.GetDocument("$set".AsSpan(), arena);
        Assert.Equal((int)NumericStatus.Inactive, setDoc.GetInt32("Numeric"));
        Assert.Equal(nameof(StringStatus.Inactive), setDoc.GetString("StringAttributed"));
    }

    public class NullableIntPoco
    {
        public int? Value { get; set; }
    }

    [Fact]
    public void Nullable_Numeric_Property_Diff_Detects_Null_To_Value_Transition()
    {
        // Same class of bug as above, but for the pre-existing (non-enum) nullable-numeric path -
        // fixed as a side effect of sharing the same guard.
        using var arena = new ArenaAllocator();
        var entity = new NullableIntPoco { Value = null };

        var writer = new ArenaBsonWriter(arena);
        DynamicBlittableSerializer<NullableIntPoco>.SerializeDelegate(ref writer, entity);
        var snapshot = writer.Commit(arena);

        entity.Value = 42;
        var builder = new ArenaUpdateDefinitionBuilder(arena);
        DynamicBlittableSerializer<NullableIntPoco>.BuildUpdateDelegate(entity, snapshot, ref builder, arena, default);

        Assert.True(builder.HasChanges);
        var doc = builder.Build();
        var setDoc = doc.GetDocument("$set".AsSpan(), arena);
        Assert.Equal(42, setDoc.GetInt32("Value"));
    }

    public class GetOnlyIdPoco
    {
        // DynamicBlittableSerializer's static ctor eagerly compiles all four delegates, including
        // Deserialize (Expression.New(type)), which needs a public parameterless constructor
        // regardless of whether this test exercises that delegate - unrelated pre-existing
        // requirement of the library, not something this test is trying to cover.
        public GetOnlyIdPoco()
        {
        }

        public GetOnlyIdPoco(string id, string name)
        {
            Id = id;
            Name = name;
        }

        public string Id { get; } = "";
        public string Name { get; set; } = "";
    }

    [Fact]
    public void Get_Only_Id_Property_Is_Still_Serialized_As_Underscore_Id()
    {
        // GetValidProperties requires CanWrite (Tier A), but EntityIdAccessor's BuildGetter reads a
        // get-only Id without requiring a setter - if the fast route silently dropped it, the
        // document would be written with no "_id" and Mongo would assign its own ObjectId.
        using var arena = new ArenaAllocator();
        var poco = new GetOnlyIdPoco("abc-123", "Test");

        var writer = new ArenaBsonWriter(arena);
        DynamicBlittableSerializer<GetOnlyIdPoco>.SerializeDelegate(ref writer, poco);
        var doc = writer.Commit(arena);

        Assert.Equal("abc-123", doc.GetString("_id"));
        Assert.Equal("Test", doc.GetString("Name"));
    }

    public class NonStringKeyedDictionaryPoco
    {
        public Dictionary<int, string> Values { get; set; } = new();
    }

    [Fact]
    public void Non_String_Keyed_Dictionary_Throws_On_First_Use()
    {
        var ex = Assert.Throws<TypeInitializationException>(() =>
        {
            using var arena = new ArenaAllocator();
            var writer = new ArenaBsonWriter(arena);
            DynamicBlittableSerializer<NonStringKeyedDictionaryPoco>.SerializeDelegate(ref writer, new NonStringKeyedDictionaryPoco());
        });

        Assert.IsType<NotSupportedException>(ex.InnerException);
        Assert.Contains("string-keyed", ex.InnerException!.Message);
    }
}
