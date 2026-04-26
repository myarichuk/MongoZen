using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace MongoZen.SourceGenerator.Tests;

public class ShadowStructsGeneratorTests
{
    private static CSharpCompilation CreateCompilation(string source)
    {
        var references = new List<MetadataReference>();

        // Add .NET runtime assemblies
        var runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        foreach (var dll in new[]
                 {
                     "System.Runtime.dll",
                     "System.Collections.dll",
                     "System.Threading.Tasks.dll",
                     "netstandard.dll",
                     "System.Linq.dll",
                     "System.Runtime.InteropServices.dll"
                 })
        {
            var path = Path.Combine(runtimeDir, dll);
            if (File.Exists(path))
            {
                references.Add(MetadataReference.CreateFromFile(path));
            }
        }

        references.Add(MetadataReference.CreateFromFile(typeof(object).Assembly.Location));

        // MongoDB.Bson — needed so ObjectId resolves as a value type (IsValueType == true)
        references.Add(MetadataReference.CreateFromFile(typeof(MongoDB.Bson.ObjectId).Assembly.Location));
        // MongoZen — needed so DbContext resolves for the ShadowStructsGenerator pipeline
        references.Add(MetadataReference.CreateFromFile(typeof(MongoZen.DbContext).Assembly.Location));
        
        var tree = CSharpSyntaxTree.ParseText(source);
        return CSharpCompilation.Create(
            "TestAssembly",
            new[] { tree },
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    }

    [Fact]
    public void GeneratesShadowStructForSimpleEntity()
    {
        var source = @"
using MongoZen;

public class User : DbContext {
    public IDbSet<Person> People { get; set; }
}

public class Person {
    public string Id { get; set; }
    public string Name { get; set; }
    public int Age { get; set; }
}";

        var generator = new ShadowStructsGenerator();
        GeneratorDriver driver = CSharpGeneratorDriver.Create(generator);
        var compilation = CreateCompilation(source);
        
        driver = driver.RunGenerators(compilation);
        var result = driver.GetRunResult();
        
        var personShadow = result.Results[0].GeneratedSources.FirstOrDefault(s => s.HintName == "Person_Shadow.g.cs");
        Assert.NotEqual(default, personShadow);
        
        var code = personShadow.SourceText.ToString();
        Assert.Contains("public struct Person_Shadow", code);
        Assert.Contains("public global::MongoZen.Collections.ArenaString Id;", code);
        Assert.Contains("public global::MongoZen.Collections.ArenaString Name;", code);
        Assert.Contains("public int Age;", code);
    }

    [Fact]
    public void GeneratesShadowStructWithRecursion()
    {
        var source = @"
using MongoZen;
using System.Collections.Generic;

public class MyContext : DbContext {
    public IDbSet<Order> Orders { get; set; }
}

public class Order {
    public string Id { get; set; }
    public Customer Customer { get; set; }
}

public class Customer {
    public string Name { get; set; }
    public Address Address { get; set; }
}

public class Address {
    public string Street { get; set; }
}";

        var generator = new ShadowStructsGenerator();
        GeneratorDriver driver = CSharpGeneratorDriver.Create(generator);
        var compilation = CreateCompilation(source);
        
        driver = driver.RunGenerators(compilation);
        var result = driver.GetRunResult();
        
        Assert.Contains(result.Results[0].GeneratedSources, s => s.HintName == "Order_Shadow.g.cs");
        Assert.Contains(result.Results[0].GeneratedSources, s => s.HintName == "Customer_Shadow.g.cs");
        Assert.Contains(result.Results[0].GeneratedSources, s => s.HintName == "Address_Shadow.g.cs");

        var orderCode = result.Results[0].GeneratedSources.First(s => s.HintName == "Order_Shadow.g.cs").SourceText.ToString();
        Assert.Contains("public Customer_Shadow Customer;", orderCode);
        Assert.Contains("public bool HasValue_Customer;", orderCode);
    }

    [Fact]
    public void BsonReferenceTypes_AreNotTreatedAsPrimitives()
    {
        // BsonDocument is in the MongoDB.Bson namespace but is a reference type.
        // The shadow generator must NOT treat it as a primitive — doing so would copy
        // the reference instead of the value, making dirty-checking silently fail.
        var source = @"
using MongoZen;
using MongoDB.Bson;

public class MyContext : DbContext {
    public IDbSet<Thing> Things { get; set; }
}

public class Thing {
    public string Id { get; set; }
    public BsonDocument ExtraData { get; set; }
}";

        var generator = new ShadowStructsGenerator();
        GeneratorDriver driver = CSharpGeneratorDriver.Create(generator);
        var compilation = CreateCompilation(source);

        driver = driver.RunGenerators(compilation);
        var result = driver.GetRunResult();

        // The generator should still produce Thing_Shadow.g.cs even though BsonDocument
        // is an unshadowable reference type — the property is simply skipped.
        var thingSource = result.Results[0].GeneratedSources
            .FirstOrDefault(s => s.HintName == "Thing_Shadow.g.cs");
        Assert.NotEqual(default, thingSource);
        var thingCode = thingSource.SourceText.ToString();

        // BsonDocument must NOT appear as a direct field: that would copy the reference
        // and make dirty-checking silently fail. The generator skips unshadowable properties.
        Assert.DoesNotContain("BsonDocument ExtraData", thingCode);
        // The Id property (a string) should still be in the shadow.
        Assert.Contains("ArenaString Id", thingCode);
    }

    [Fact]
    public void BsonValueTypes_AreTreatedAsPrimitives()
    {
        // ObjectId is a struct in MongoDB.Bson — it should be a primitive (direct copy safe).
        var source = @"
using MongoZen;
using MongoDB.Bson;

public class MyContext : DbContext {
    public IDbSet<Thing> Things { get; set; }
}

public class Thing {
    public ObjectId Id { get; set; }
    public string Name { get; set; }
}";

        var generator = new ShadowStructsGenerator();
        GeneratorDriver driver = CSharpGeneratorDriver.Create(generator);
        var compilation = CreateCompilation(source);

        driver = driver.RunGenerators(compilation);
        var result = driver.GetRunResult();

        var thingCode = result.Results[0].GeneratedSources
            .First(s => s.HintName == "Thing_Shadow.g.cs").SourceText.ToString();

        // ObjectId is a value type — should be copied directly (primitive).
        // The generator emits the full metadata name as returned by the symbol display.
        Assert.Contains("ObjectId Id;", thingCode);
        // Must NOT have a HasValue_ companion (that's only for reference-type properties).
        Assert.DoesNotContain("HasValue_Id", thingCode);
    }

    [Fact]
    public void GeneratesShadowStructWithCollections()
    {
        var source = @"
using MongoZen;
using System.Collections.Generic;

public class MyContext : DbContext {
    public IDbSet<Blog> Blogs { get; set; }
}

public class Blog {
    public string Id { get; set; }
    public List<string> Tags { get; set; }
    public Post[] Posts { get; set; }
}

public class Post {
    public string Title { get; set; }
}";

        var generator = new ShadowStructsGenerator();
        GeneratorDriver driver = CSharpGeneratorDriver.Create(generator);
        var compilation = CreateCompilation(source);
        
        driver = driver.RunGenerators(compilation);
        var result = driver.GetRunResult();
        
        var blogCode = result.Results[0].GeneratedSources.First(s => s.HintName == "Blog_Shadow.g.cs").SourceText.ToString();
        Assert.Contains("public SharpArena.Collections.ArenaList<global::MongoZen.Collections.ArenaString> Tags;", blogCode);
        Assert.Contains("public SharpArena.Collections.ArenaList<Post_Shadow> Posts;", blogCode);
    }

    [Fact]
    public void GeneratesShadowStructWithDictionaryAndBlockScopes()
    {
        var source = @"
using MongoZen;
using System.Collections.Generic;

public class MyContext : DbContext {
    public IDbSet<User> Users { get; set; }
}

public class User {
    public string Id { get; set; }
    public Dictionary<string, string> Settings { get; set; }
    public List<int> Scores { get; set; }
}";

        var generator = new ShadowStructsGenerator();
        GeneratorDriver driver = CSharpGeneratorDriver.Create(generator);
        var compilation = CreateCompilation(source);
        
        driver = driver.RunGenerators(compilation);
        var result = driver.GetRunResult();
        
        var userCode = result.Results[0].GeneratedSources.First(s => s.HintName == "User_Shadow.g.cs").SourceText.ToString();
        
        // Verify block scopes in From method for collections/dictionaries
        Assert.Contains("{", userCode); 
        // We look for the pattern where shadowItem/shadowPair is assigned within a block
        Assert.Contains("foreach (var item in source.Scores)", userCode);
        Assert.Contains("var shadowItem", userCode);
        
        // Verify Dictionary dirty check is order-independent (contains nested loop and foundKey flag)
        Assert.Contains("foreach (var kvp in current.Settings)", userCode);
        Assert.Contains("bool foundKey = false;", userCode);
        Assert.Contains("for (int j = 0; j < shadow.Settings.Length; j++)", userCode);
        Assert.Contains("if (shadowPair.Key.Equals(kvp.Key))", userCode);
    }

    private static string Normalize(string s) => s.Replace("\r\n", "\n");
}
