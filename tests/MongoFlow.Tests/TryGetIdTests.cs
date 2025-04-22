using MongoDB.Bson.Serialization.Attributes;

namespace MongoFlow.Tests;

public class TryGetIdTests
{
    private class NoIdClass
    {
        public string Name { get; set; }
    }

    private class IdByName
    {
        public Guid Id { get; set; } = Guid.NewGuid();
    }

    private class BsonIdClass
    {
        [BsonId]
        public string CustomId { get; set; } = "abc123";
    }

    private class BothIdClass
    {
        public string Id { get; set; } = "shouldNotBeUsed";

        [BsonId]
        public string CustomId { get; set; } = "bson-wins";
    }

    [Fact]
    public void TryGetId_ShouldReturnFalse_WhenObjectIsNull()
    {
        object obj = null;

        var result = obj.TryGetId(out var id);

        Assert.False(result);
        Assert.Null(id);
    }

    [Fact]
    public void TryGetId_ShouldReturnFalse_WhenNoIdPropertyExists()
    {
        var obj = new NoIdClass { Name = "Test" };

        var result = obj.TryGetId(out var id);

        Assert.False(result);
        Assert.Null(id);
    }

    [Fact]
    public void TryGetId_ShouldReturnTrue_WhenIdPropertyExists()
    {
        var obj = new IdByName();
        var expectedId = obj.Id;

        var result = obj.TryGetId(out var id);

        Assert.True(result);
        Assert.Equal(expectedId, id);
    }

    [Fact]
    public void TryGetId_ShouldReturnTrue_WhenBsonIdAttributeExists()
    {
        var obj = new BsonIdClass { CustomId = "xyz789" };

        var result = obj.TryGetId(out var id);

        Assert.True(result);
        Assert.Equal("xyz789", id);
    }

    [Fact]
    public void TryGetId_ShouldPreferBsonIdAttribute_OverIdName()
    {
        var obj = new BothIdClass
        {
            Id = "nope",
            CustomId = "yes!"
        };

        var result = obj.TryGetId(out var id);

        Assert.True(result);
        Assert.Equal("yes!", id); // BsonId takes precedence
    }

    [Fact]
    public void TryGetId_ShouldReturnFalse_WhenIdValueIsNull()
    {
        var obj = new BsonIdClass { CustomId = null };

        var result = obj.TryGetId(out var id);

        Assert.False(result); // property found, but value is null
        Assert.Null(id);
    }

    [Fact]
    public void TryGetId_ShouldHandleMultipleCalls_ForSameType()
    {
        var obj1 = new IdByName();
        var obj2 = new IdByName();

        var result1 = obj1.TryGetId(out var id1);
        var result2 = obj2.TryGetId(out var id2);

        Assert.True(result1);
        Assert.True(result2);
        Assert.Equal(obj1.Id, id1);
        Assert.Equal(obj2.Id, id2);
    }
}
