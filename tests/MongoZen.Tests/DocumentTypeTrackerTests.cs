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
        Assert.Equal("ExplicitDocs", DocumentTypeTracker.GetDefaultCollectionName(typeof(ExplicitDoc)));
        Assert.Equal("CustomCollection", DocumentTypeTracker.GetDefaultCollectionName(typeof(CustomDoc)));
        
        Assert.Equal("Categories", DocumentTypeTracker.GetDefaultCollectionName(typeof(Category)));
        Assert.Equal("Foxes", DocumentTypeTracker.GetDefaultCollectionName(typeof(Fox)));
        Assert.Equal("Statuses", DocumentTypeTracker.GetDefaultCollectionName(typeof(Status)));
        Assert.Equal("Leaves", DocumentTypeTracker.GetDefaultCollectionName(typeof(Leaf)));
        Assert.Equal("Wives", DocumentTypeTracker.GetDefaultCollectionName(typeof(Wife)));
        Assert.Equal("Days", DocumentTypeTracker.GetDefaultCollectionName(typeof(Day)));
    }

    [Fact]
    public void Should_Respect_Global_Convention_Override()
    {
        var original = Conventions.FindCollectionName;
        DocumentTypeTracker.ClearCache();
        try
        {
            Conventions.FindCollectionName = type => "Global_" + type.Name;
            Assert.Equal("Global_ExplicitDoc", DocumentTypeTracker.GetDefaultCollectionName(typeof(ExplicitDoc)));
        }
        finally
        {
            Conventions.FindCollectionName = original;
            DocumentTypeTracker.ClearCache();
        }
    }
}
