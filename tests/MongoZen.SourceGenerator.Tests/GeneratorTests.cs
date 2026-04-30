using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Testing;
using Xunit;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Threading.Tasks;
using System.IO;

namespace MongoZen.SourceGenerator.Tests;

public class GeneratorTests
{
    private const string AttributeSource = @"
namespace MongoZen;

[System.AttributeUsage(System.AttributeTargets.Class | System.AttributeTargets.Struct)]
public class DocumentAttribute : System.Attribute { }
";

    [Fact]
    public async Task Should_Generate_Simple_Shadow()
    {
        var inputSource = @"
using MongoZen;

namespace TestNamespace;

[Document]
public class SimpleEntity
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}
";

        var test = new CSharpSourceGeneratorTest<ShadowGenerator, DefaultVerifier>
        {
            TestState =
            {
                Sources = { AttributeSource, inputSource },
                GeneratedSources =
                {
                    (typeof(ShadowGenerator), "SimpleEntity.Shadow.g.cs", @"#nullable enable
using System;
using System.Collections.Generic;
using MongoZen;
using SharpArena.Allocators;
using SharpArena.Collections;
using MongoDB.Driver;
using MongoDB.Bson;

namespace TestNamespace;

public readonly unsafe struct SimpleEntityShadow
{
    public readonly bool _HasValue;
    public readonly int Id;
    public readonly ArenaUtf8String Name;

    public static SimpleEntityShadow Create(TestNamespace.SimpleEntity? entity, ArenaAllocator arena)
    {
        if (entity == null) return default;

        return new SimpleEntityShadow(
            true,
            entity.Id,
            entity.Name == null ? default : ArenaUtf8String.Clone(entity.Name, arena)
        );
    }

    private SimpleEntityShadow(bool hasValue,
        int id,
        ArenaUtf8String name
    )
    {
        this._HasValue = hasValue;
        this.Id = id;
        this.Name = name;
    }

    public bool Equals(TestNamespace.SimpleEntity? entity)
    {
        if (entity == null) return !this._HasValue;
        if (!this._HasValue) return false;

        if (entity.Id != this.Id) return false;
        if (string.IsNullOrEmpty(entity.Name)) { if (!this.Name.IsEmpty) return false; }
        else if (!this.Name.Equals(entity.Name)) return false;
        return true;
    }

    public UpdateDefinition<BsonDocument>? BuildUpdate(TestNamespace.SimpleEntity entity, UpdateDefinitionBuilder<BsonDocument> builder)
    {
        UpdateDefinition<BsonDocument>? combined = null;
        this.BuildUpdate(entity, """", builder, ref combined);
        return combined;
    }

    public void BuildUpdate(TestNamespace.SimpleEntity? entity, string pathPrefix, UpdateDefinitionBuilder<BsonDocument> builder, ref UpdateDefinition<BsonDocument>? combined)
    {
        if (this.Equals(entity)) return;

        // Id
        if (entity != null && entity.Id != this.Id)
            combined = (combined == null) ? builder.Set((string.IsNullOrEmpty(pathPrefix) ? ""Id"" : pathPrefix + ""Id""), entity.Id) : builder.Combine(combined, builder.Set((string.IsNullOrEmpty(pathPrefix) ? ""Id"" : pathPrefix + ""Id""), entity.Id));

        // Name
        if (entity != null)
        {
            var cur = entity.Name;
            if (string.IsNullOrEmpty(cur))
            {
                if (!this.Name.IsEmpty) combined = (combined == null) ? builder.Unset((string.IsNullOrEmpty(pathPrefix) ? ""Name"" : pathPrefix + ""Name"")) : builder.Combine(combined, builder.Unset((string.IsNullOrEmpty(pathPrefix) ? ""Name"" : pathPrefix + ""Name"")));
            }
            else if (!this.Name.Equals(cur))
            {
                combined = (combined == null) ? builder.Set((string.IsNullOrEmpty(pathPrefix) ? ""Name"" : pathPrefix + ""Name""), cur) : builder.Combine(combined, builder.Set((string.IsNullOrEmpty(pathPrefix) ? ""Name"" : pathPrefix + ""Name""), cur));
            }
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

    [Fact]
    public async Task Should_Generate_Complex_Shadow()
    {
        var inputSource = @"
using System.Collections.Generic;
using MongoZen;

namespace TestNamespace;

public class Address
{
    public string City { get; set; } = string.Empty;
}

[Document]
public class ComplexEntity
{
    public int Id { get; set; }
    public Address Home { get; set; } = new();
    public List<string> Tags { get; set; } = new();
}
";

        var test = new CSharpSourceGeneratorTest<ShadowGenerator, DefaultVerifier>
        {
            TestState =
            {
                Sources = { AttributeSource, inputSource },
                GeneratedSources =
                {
                    (typeof(ShadowGenerator), "ComplexEntity.Shadow.g.cs", @"#nullable enable
using System;
using System.Collections.Generic;
using MongoZen;
using SharpArena.Allocators;
using SharpArena.Collections;
using MongoDB.Driver;
using MongoDB.Bson;

namespace TestNamespace;

public readonly unsafe struct ComplexEntityShadow
{
    public readonly bool _HasValue;
    public readonly int Id;
    public readonly AddressShadow Home;
    public readonly ArenaList<ArenaUtf8String> Tags;

    public static ComplexEntityShadow Create(TestNamespace.ComplexEntity? entity, ArenaAllocator arena)
    {
        if (entity == null) return default;

        var Tags_cloned = default(ArenaList<ArenaUtf8String>);
        if (entity.Tags != null)
        {
            Tags_cloned = new ArenaList<ArenaUtf8String>(arena, entity.Tags.Count);
            foreach (var item in entity.Tags)
            {
                Tags_cloned.Add(item == null ? default : ArenaUtf8String.Clone(item, arena));
            }
        }
        return new ComplexEntityShadow(
            true,
            entity.Id,
            AddressShadow.Create(entity.Home, arena),
            Tags_cloned
        );
    }

    private ComplexEntityShadow(bool hasValue,
        int id,
        AddressShadow home,
        ArenaList<ArenaUtf8String> tags
    )
    {
        this._HasValue = hasValue;
        this.Id = id;
        this.Home = home;
        this.Tags = tags;
    }

    public bool Equals(TestNamespace.ComplexEntity? entity)
    {
        if (entity == null) return !this._HasValue;
        if (!this._HasValue) return false;

        if (entity.Id != this.Id) return false;
        if (!this.Home.Equals(entity.Home)) return false;
        if (!IsTagsEqual(entity.Tags)) return false;
        return true;
    }

    public UpdateDefinition<BsonDocument>? BuildUpdate(TestNamespace.ComplexEntity entity, UpdateDefinitionBuilder<BsonDocument> builder)
    {
        UpdateDefinition<BsonDocument>? combined = null;
        this.BuildUpdate(entity, """", builder, ref combined);
        return combined;
    }

    public void BuildUpdate(TestNamespace.ComplexEntity? entity, string pathPrefix, UpdateDefinitionBuilder<BsonDocument> builder, ref UpdateDefinition<BsonDocument>? combined)
    {
        if (this.Equals(entity)) return;

        // Id
        if (entity != null && entity.Id != this.Id)
            combined = (combined == null) ? builder.Set((string.IsNullOrEmpty(pathPrefix) ? ""Id"" : pathPrefix + ""Id""), entity.Id) : builder.Combine(combined, builder.Set((string.IsNullOrEmpty(pathPrefix) ? ""Id"" : pathPrefix + ""Id""), entity.Id));

        // Home
        var child_Home = entity.Home;
        if (child_Home == null)
        {
            if (this.Home._HasValue) combined = (combined == null) ? builder.Unset((string.IsNullOrEmpty(pathPrefix) ? ""Home"" : pathPrefix + ""Home"")) : builder.Combine(combined, builder.Unset((string.IsNullOrEmpty(pathPrefix) ? ""Home"" : pathPrefix + ""Home"")));
        }
        else if (!this.Home._HasValue)
        {
            combined = (combined == null) ? builder.Set((string.IsNullOrEmpty(pathPrefix) ? ""Home"" : pathPrefix + ""Home""), child_Home) : builder.Combine(combined, builder.Set((string.IsNullOrEmpty(pathPrefix) ? ""Home"" : pathPrefix + ""Home""), child_Home));
        }
        else
        {
            this.Home.BuildUpdate(child_Home, (string.IsNullOrEmpty(pathPrefix) ? ""Home"" : pathPrefix + ""Home"") + ""."", builder, ref combined);
        }

        // Tags
        var coll_Tags = entity.Tags;
        if (entity != null)
        {
            if (coll_Tags == null)
            {
                if (this.Tags.Length != 0) combined = (combined == null) ? builder.Unset((string.IsNullOrEmpty(pathPrefix) ? ""Tags"" : pathPrefix + ""Tags"")) : builder.Combine(combined, builder.Unset((string.IsNullOrEmpty(pathPrefix) ? ""Tags"" : pathPrefix + ""Tags"")));
            }
            else if (!IsTagsEqual(coll_Tags))
            {
                combined = (combined == null) ? builder.Set((string.IsNullOrEmpty(pathPrefix) ? ""Tags"" : pathPrefix + ""Tags""), coll_Tags) : builder.Combine(combined, builder.Set((string.IsNullOrEmpty(pathPrefix) ? ""Tags"" : pathPrefix + ""Tags""), coll_Tags));
            }
        }
    }

    private bool IsTagsEqual(System.Collections.Generic.List<string>? current)
    {
        if (current == null) return this.Tags.Length == 0;
        if (current.Count != this.Tags.Length) return false;
        var idx = 0;
        var span = this.Tags.AsReadOnlySpan();
        foreach (var item in current)
        {
            var s = span[idx++];
                if (string.IsNullOrEmpty(item)) { if (!s.IsEmpty) return false; }
                else if (!s.Equals(item)) return false;
        }
        return true;
    }
}
"),
                    (typeof(ShadowGenerator), "Address.Shadow.g.cs", @"#nullable enable
using System;
using System.Collections.Generic;
using MongoZen;
using SharpArena.Allocators;
using SharpArena.Collections;
using MongoDB.Driver;
using MongoDB.Bson;

namespace TestNamespace;

public readonly unsafe struct AddressShadow
{
    public readonly bool _HasValue;
    public readonly ArenaUtf8String City;

    public static AddressShadow Create(TestNamespace.Address? entity, ArenaAllocator arena)
    {
        if (entity == null) return default;

        return new AddressShadow(
            true,
            entity.City == null ? default : ArenaUtf8String.Clone(entity.City, arena)
        );
    }

    private AddressShadow(bool hasValue,
        ArenaUtf8String city
    )
    {
        this._HasValue = hasValue;
        this.City = city;
    }

    public bool Equals(TestNamespace.Address? entity)
    {
        if (entity == null) return !this._HasValue;
        if (!this._HasValue) return false;

        if (string.IsNullOrEmpty(entity.City)) { if (!this.City.IsEmpty) return false; }
        else if (!this.City.Equals(entity.City)) return false;
        return true;
    }

    public UpdateDefinition<BsonDocument>? BuildUpdate(TestNamespace.Address entity, UpdateDefinitionBuilder<BsonDocument> builder)
    {
        UpdateDefinition<BsonDocument>? combined = null;
        this.BuildUpdate(entity, """", builder, ref combined);
        return combined;
    }

    public void BuildUpdate(TestNamespace.Address? entity, string pathPrefix, UpdateDefinitionBuilder<BsonDocument> builder, ref UpdateDefinition<BsonDocument>? combined)
    {
        if (this.Equals(entity)) return;

        // City
        if (entity != null)
        {
            var cur = entity.City;
            if (string.IsNullOrEmpty(cur))
            {
                if (!this.City.IsEmpty) combined = (combined == null) ? builder.Unset((string.IsNullOrEmpty(pathPrefix) ? ""City"" : pathPrefix + ""City"")) : builder.Combine(combined, builder.Unset((string.IsNullOrEmpty(pathPrefix) ? ""City"" : pathPrefix + ""City"")));
            }
            else if (!this.City.Equals(cur))
            {
                combined = (combined == null) ? builder.Set((string.IsNullOrEmpty(pathPrefix) ? ""City"" : pathPrefix + ""City""), cur) : builder.Combine(combined, builder.Set((string.IsNullOrEmpty(pathPrefix) ? ""City"" : pathPrefix + ""City""), cur));
            }
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
