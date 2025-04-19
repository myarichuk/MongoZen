using MongoDB.Bson.Serialization.Attributes;
using System.Reflection;

namespace Library.Tests;

// here we change global static state, so we don't run those concurrently
[Collection("NoConcurrency")]
public class EntityIdAccessorTests
{

    private class WithBsonId
    {
        [BsonId]
        public int CustomId { get; set; }
    }

    private class WithDefaultId
    {
        public Guid Id { get; set; }
    }

    private class WithoutId {}

    [Fact]
    public void ResolveId_WithBsonId_Works()
    {
        var prop = GlobalIdConventionProvider.Convention.ResolveIdProperty<WithBsonId>();
        Assert.NotNull(prop);
        Assert.Equal("CustomId", prop!.Name);
    }

    [Fact]
    public void ResolveId_DefaultIdName_Works()
    {
        var prop = GlobalIdConventionProvider.Convention.ResolveIdProperty<WithDefaultId>();
        Assert.NotNull(prop);
        Assert.Equal("Id", prop!.Name);
    }

    [Fact]
    public void ResolveId_MissingProperty_ReturnsNull()
    {
        var prop = GlobalIdConventionProvider.Convention.ResolveIdProperty<WithoutId>();
        Assert.Null(prop);
    }

    [Fact]
    public void CustomConvention_OverridesDefault()
    {
        try
        {
            var custom = new FakeConvention();
            EntityIdAccessor<WithBsonId>.SetConvention(custom);

            var entity = new WithBsonId { CustomId = 42 };
            var id = EntityIdAccessor<WithBsonId>.Get(entity);

            Assert.Null(id);
        }
        finally
        {
            EntityIdAccessor<WithBsonId>.SetConvention(null);
        }
    }

    private class FakeConvention : IIdConvention
    {
        public PropertyInfo? ResolveIdProperty<TEntity>() => null;
    }
}