// Copyright (c) MyProject. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using MongoDB.Bson.Serialization.Attributes;

namespace MongoZen.Tests;

/// <summary>
/// Tests for the <see cref="ReflectionExtensions.TryGetId"/> method.
/// </summary>
public class TryGetIdTests
{
    /// <summary>
    /// Verifies that TryGetId returns false when the object is null.
    /// </summary>
    [Fact]
    public void TryGetId_ShouldReturnFalse_WhenObjectIsNull()
    {
        object? obj = null;

        var result = obj.TryGetId(out var id);

        Assert.False(result);
        Assert.Null(id);
    }

    /// <summary>
    /// Verifies that TryGetId returns false when no Id property exists.
    /// </summary>
    [Fact]
    public void TryGetId_ShouldReturnFalse_WhenNoIdPropertyExists()
    {
        var obj = new NoIdClass { Name = "Test" };

        var result = obj.TryGetId(out var id);

        Assert.False(result);
        Assert.Null(id);
    }

    /// <summary>
    /// Verifies that TryGetId returns true when an Id property exists by name.
    /// </summary>
    [Fact]
    public void TryGetId_ShouldReturnTrue_WhenIdPropertyExists()
    {
        var obj = new IdByName();
        var expectedId = obj.Id;

        var result = obj.TryGetId(out var id);

        Assert.True(result);
        Assert.Equal(expectedId, id);
    }

    /// <summary>
    /// Verifies that TryGetId returns true when a BsonId attribute exists.
    /// </summary>
    [Fact]
    public void TryGetId_ShouldReturnTrue_WhenBsonIdAttributeExists()
    {
        var obj = new BsonIdClass { CustomId = "xyz789" };

        var result = obj.TryGetId(out var id);

        Assert.True(result);
        Assert.Equal("xyz789", id);
    }

    /// <summary>
    /// Verifies that TryGetId prefers the BsonId attribute over the Id property name.
    /// </summary>
    [Fact]
    public void TryGetId_ShouldPreferBsonIdAttribute_OverIdName()
    {
        var obj = new BothIdClass
        {
            Id = "nope",
            CustomId = "yes!",
        };

        var result = obj.TryGetId(out var id);

        Assert.True(result);
        Assert.Equal("yes!", id); // BsonId takes precedence
    }

    /// <summary>
    /// Verifies that TryGetId returns false when the Id value is null.
    /// </summary>
    [Fact]
    public void TryGetId_ShouldReturnFalse_WhenIdValueIsNull()
    {
        var obj = new BsonIdClass { CustomId = null };

        var result = obj.TryGetId(out var id);

        Assert.False(result); // property found, but value is null
        Assert.Null(id);
    }

    /// <summary>
    /// Verifies that TryGetId handles multiple calls for the same type.
    /// </summary>
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

    private class NoIdClass
    {
        public string Name { get; set; } = string.Empty;
    }

    private class IdByName
    {
        public Guid Id { get; set; } = Guid.NewGuid();
    }

    private class BsonIdClass
    {
        [BsonId]
        public string? CustomId { get; set; } = "abc123";
    }

    private class BothIdClass
    {
        public string Id { get; set; } = "shouldNotBeUsed";

        [BsonId]
        public string CustomId { get; set; } = "bson-wins";
    }
}
