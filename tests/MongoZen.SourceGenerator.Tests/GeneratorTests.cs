using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Testing;
using Xunit;

namespace MongoZen.SourceGenerator.Tests;

public class GeneratorTests
{
    private const string AttributeSource = @"
namespace MongoZen;

[System.AttributeUsage(System.AttributeTargets.Class | System.AttributeTargets.Struct)]
public class DocumentAttribute : System.Attribute { }

public interface IBlittableDocument<T> 
{
    static abstract void Serialize(ref MongoZen.Bson.ArenaBsonWriter writer, T entity);
    static abstract T Deserialize(MongoZen.Bson.BlittableBsonDocument doc, SharpArena.Allocators.ArenaAllocator arena);
    static abstract MongoDB.Driver.UpdateDefinition<MongoDB.Bson.BsonDocument>? BuildUpdate(T entity, MongoZen.Bson.BlittableBsonDocument snapshot, MongoDB.Driver.UpdateDefinitionBuilder<MongoDB.Bson.BsonDocument> builder);
}
";

    private const string BsonEngineSource = @"
namespace MongoZen.Bson;
public struct ArenaBsonWriter { 
    public void WriteStartDocument() {}
    public void WriteEndDocument() {}
    public void WriteInt32(System.ReadOnlySpan<char> name, int value) {}
    public void WriteString(System.ReadOnlySpan<char> name, System.ReadOnlySpan<char> value) {}
}
public struct BlittableBsonDocument {
    public bool TryGetElementOffset(System.ReadOnlySpan<char> name, out int offset) { offset = 0; return false; }
    public int GetInt32(int offset) => 0;
    public string GetString(int offset) => """";
    public BlittableBsonDocument GetDocument(int offset, SharpArena.Allocators.ArenaAllocator arena) => default;
}
";

    [Fact]
    public async Task Should_Generate_Simple_Blittable()
    {
        var inputSource = @"
using MongoZen;

namespace TestNamespace;

[Document]
public partial class SimpleEntity
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}
";

        var test = new CSharpSourceGeneratorTest<ShadowGenerator, DefaultVerifier>
        {
            TestState =
            {
                Sources = { AttributeSource, BsonEngineSource, inputSource },
                GeneratedSources =
                {
                    (typeof(ShadowGenerator), "SimpleEntity.Blittable.g.cs", @"#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using MongoZen;
using MongoZen.Bson;
using SharpArena.Allocators;
using MongoDB.Driver;
using MongoDB.Bson;

namespace TestNamespace;

partial class SimpleEntity : IBlittableDocument<SimpleEntity>
{
    public static void Serialize(ref ArenaBsonWriter writer, SimpleEntity entity)
    {
        writer.WriteStartDocument();
        writer.WriteInt32(""_id"", entity.Id);
        if (entity.Name != null) writer.WriteString(""Name"", entity.Name.AsSpan());
        writer.WriteEndDocument();
    }

    public static SimpleEntity Deserialize(BlittableBsonDocument doc, ArenaAllocator arena)
    {
        var entity = new SimpleEntity();
        if (doc.TryGetElementOffset(""_id"", out var offset_Id))
        {
            entity.Id = doc.GetInt32(offset_Id);
        }
        if (doc.TryGetElementOffset(""Name"", out var offset_Name))
        {
            entity.Name = doc.GetString(offset_Name);
        }
        return entity;
    }

    public static UpdateDefinition<BsonDocument>? BuildUpdate(SimpleEntity entity, BlittableBsonDocument snapshot, UpdateDefinitionBuilder<BsonDocument> builder)
    {
        UpdateDefinition<BsonDocument>? combined = null;
        if (snapshot.TryGetElementOffset(""_id"", out var off_Id))
        {
            if (entity.Id != snapshot.GetInt32(off_Id))
                combined = (combined == null) ? builder.Set(""_id"", entity.Id) : builder.Combine(combined, builder.Set(""_id"", entity.Id));
        }
        if (snapshot.TryGetElementOffset(""Name"", out var off_Name))
        {
            if (!object.Equals(snapshot.GetString(off_Name), entity.Name))
                combined = (combined == null) ? builder.Set(""Name"", entity.Name) : builder.Combine(combined, builder.Set(""Name"", entity.Name));
        }
        return combined;
    }
}
")
                },
                ReferenceAssemblies = new ReferenceAssemblies(
                    "net10.0",
                    new PackageIdentity("Microsoft.NETCore.App.Ref", "10.0.0-preview.1.25080.5"),
                    Path.Combine("ref", "net10.0"))
            }
        };

        test.TestState.AdditionalReferences.Add(typeof(MongoDB.Bson.BsonDocument).Assembly);
        test.TestState.AdditionalReferences.Add(typeof(MongoDB.Driver.UpdateDefinition<>).Assembly);
        test.TestState.AdditionalReferences.Add(typeof(SharpArena.Allocators.ArenaAllocator).Assembly);

        await test.RunAsync();
    }
}
