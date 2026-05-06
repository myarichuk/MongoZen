using System.Reflection;
using Xunit;

namespace MongoZen.Tests;

public class DocumentTypeTrackerTests
{
    [Document]
    public class ExplicitDoc { }

    public class ImplicitDoc { }

    [Document(CollectionName = "CustomCollection")]
    public class CustomDoc { }

    public class Category { }
    public class Fox { }
    public class Status { }
    public class Leaf { }
    public class Wife { }
    public class Day { }

    [Fact]
    public void Should_Find_Document_Types()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var types = DocumentTypeTracker.GetDocumentTypes(assembly).ToList();

        Assert.Contains(typeof(ExplicitDoc), types);
        // Note: StringEntity and ElementEntity were removed or renamed in previous refactors.
        // We'll rely on ExplicitDoc for this test.
    }

    [Fact]
    public void Should_Infer_Default_Collection_Names()
    {
        var conventions = new DocumentConventions();
        Assert.Equal("ExplicitDocs", conventions.GetCollectionName(typeof(ExplicitDoc)));
        Assert.Equal("CustomCollection", conventions.GetCollectionName(typeof(CustomDoc)));
        
        Assert.Equal("Categories", conventions.GetCollectionName(typeof(Category)));
        Assert.Equal("Foxes", conventions.GetCollectionName(typeof(Fox)));
        Assert.Equal("Statuses", conventions.GetCollectionName(typeof(Status)));
        Assert.Equal("Leaves", conventions.GetCollectionName(typeof(Leaf)));
        Assert.Equal("Wives", conventions.GetCollectionName(typeof(Wife)));
        Assert.Equal("Days", conventions.GetCollectionName(typeof(Day)));
    }

    [Fact]
    public void Should_Respect_Convention_Override()
    {
        var conventions = new DocumentConventions
        {
            FindCollectionName = type => "Prefix_" + type.Name
        };
        Assert.Equal("Prefix_ExplicitDoc", conventions.GetCollectionName(typeof(ExplicitDoc)));
    }
}
