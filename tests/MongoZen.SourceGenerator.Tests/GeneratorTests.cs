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
    static abstract void DeserializeInto(MongoZen.Bson.BlittableBsonDocument doc, SharpArena.Allocators.ArenaAllocator arena, T entity);
    static abstract void BuildUpdate(T entity, MongoZen.Bson.BlittableBsonDocument snapshot, ref MongoZen.ChangeTracking.ArenaUpdateDefinitionBuilder builder, SharpArena.Allocators.ArenaAllocator arena, System.ReadOnlySpan<char> pathPrefix);
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

    private const string ChangeTrackingSource = @"
namespace MongoZen.ChangeTracking;
public struct ArenaUpdateDefinitionBuilder {
    public void Set<T>(System.ReadOnlySpan<char> path, T value) {}
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
                Sources = { AttributeSource, BsonEngineSource, ChangeTrackingSource, inputSource },
                GeneratedSources =
                {
                    (typeof(ShadowGenerator), "SimpleEntity.Blittable.g.cs", @"#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using MongoZen;
using MongoZen.Bson;
using MongoZen.ChangeTracking;
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

    public static void DeserializeInto(BlittableBsonDocument doc, ArenaAllocator arena, SimpleEntity entity)
    {
        if (doc.TryGetElementOffset(""_id"", out var offset_Id))
        {
            entity.Id = doc.GetInt32(offset_Id);
        }
        if (doc.TryGetElementOffset(""Name"", out var offset_Name))
        {
            entity.Name = doc.GetString(offset_Name);
        }
    }

    public static void BuildUpdate(SimpleEntity entity, BlittableBsonDocument snapshot, ref ArenaUpdateDefinitionBuilder builder, SharpArena.Allocators.ArenaAllocator arena, ReadOnlySpan<char> pathPrefix)
    {
        if (snapshot.TryGetElementOffset(""_id"", out var off_Id))
        {
            Span<char> fullPath_Id = stackalloc char[pathPrefix.Length + 3 + 1];
            int len_Id = 0;
            if (pathPrefix.Length > 0) { pathPrefix.CopyTo(fullPath_Id); fullPath_Id[pathPrefix.Length] = '.'; len_Id = pathPrefix.Length + 1; }
            ""_id"".AsSpan().CopyTo(fullPath_Id.Slice(len_Id));
            var path_Id = fullPath_Id.Slice(0, len_Id + 3);
            if (entity.Id != snapshot.GetInt32(off_Id))
                builder.Set(path_Id, entity.Id);
        }
        if (snapshot.TryGetElementOffset(""Name"", out var off_Name))
        {
            Span<char> fullPath_Name = stackalloc char[pathPrefix.Length + 4 + 1];
            int len_Name = 0;
            if (pathPrefix.Length > 0) { pathPrefix.CopyTo(fullPath_Name); fullPath_Name[pathPrefix.Length] = '.'; len_Name = pathPrefix.Length + 1; }
            ""Name"".AsSpan().CopyTo(fullPath_Name.Slice(len_Name));
            var path_Name = fullPath_Name.Slice(0, len_Name + 4);
            if (!object.Equals(snapshot.GetString(off_Name), entity.Name))
                builder.Set(path_Name, entity.Name);
        }
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
