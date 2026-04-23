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
        Assert.Contains("public SharpArena.Collections.ArenaString Id;", code);
        Assert.Contains("public SharpArena.Collections.ArenaString Name;", code);
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
        Assert.Contains("public SharpArena.Collections.ArenaList<SharpArena.Collections.ArenaString> Tags;", blogCode);
        Assert.Contains("public SharpArena.Collections.ArenaList<Post_Shadow> Posts;", blogCode);
    }

    private static string Normalize(string s) => s.Replace("\r\n", "\n");
}
