using MongoDB.Bson;
using Xunit;

namespace MongoZen.Tests;

[CollectionDefinition(nameof(GuidConventionCollection), DisableParallelization = true)]
public class GuidConventionCollection
{
}

[Collection(nameof(GuidConventionCollection))]
public class GuidConventionRegistrationTests : IntegrationTestBase
{
    [Fact]
    public void Second_Store_With_Same_GuidRepresentation_Is_Idempotent_NoOp()
    {
        var conventions = new DocumentConventions { GuidRepresentation = GuidRepresentation.Standard };

        using var store1 = new DocumentStore(Client, Database.DatabaseNamespace.DatabaseName, conventions);
        using var store2 = new DocumentStore(Client, Database.DatabaseNamespace.DatabaseName, conventions);
    }

    [Fact]
    public void Store_With_Differing_GuidRepresentation_Throws()
    {
        // Lock the process-global registration to Standard first (this is what every other
        // store in the suite already uses, but the test is self-contained regardless of order).
        using var store1 = new DocumentStore(Client, Database.DatabaseNamespace.DatabaseName,
            new DocumentConventions { GuidRepresentation = GuidRepresentation.Standard });

        var ex = Assert.Throws<InvalidOperationException>(() =>
            new DocumentStore(Client, Database.DatabaseNamespace.DatabaseName,
                new DocumentConventions { GuidRepresentation = GuidRepresentation.CSharpLegacy }));

        Assert.Contains("GuidRepresentation", ex.Message);
    }
}
