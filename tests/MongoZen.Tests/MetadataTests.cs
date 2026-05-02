using Xunit;
using MongoZen.Bson;

namespace MongoZen.Tests;

public class MetadataTests
{
    [Fact]
    public void PrintMetadata()
    {
        MetadataChecker.Check();
    }
}
