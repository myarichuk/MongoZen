using System.Reflection;
using Xunit;

namespace MongoZen.Tests;

public class DocumentTypeTrackerTests
{
    [Document]
    public class ExplicitDoc { }

    public class ImplicitDoc { }

    [Fact]
    public void Should_Find_Document_Types()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var types = DocumentTypeTracker.GetDocumentTypes(assembly).ToList();

        Assert.Contains(typeof(ExplicitDoc), types);
        Assert.Contains(typeof(TrackingTests.StringEntity), types);
        Assert.Contains(typeof(TrackingTests.ElementEntity), types);
        Assert.Contains(typeof(TrackingTests.Zoo), types);
    }
}
