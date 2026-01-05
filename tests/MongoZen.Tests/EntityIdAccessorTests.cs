// Copyright (c) 2024-present, Shuai Wang. All rights reserved.
// Use of this source code is governed by an MIT-style license that can be
// found in the LICENSE file.

using System.Reflection;
using MongoDB.Bson.Serialization.Attributes;

namespace MongoZen.Tests;

/// <summary>
/// Tests for the <see cref="EntityIdAccessor{TEntity}"/> class.
/// </summary>
[Collection("NoConcurrency")]
public class EntityIdAccessorTests
{
    /// <summary>
    /// Verifies that the Id property is correctly resolved when the [BsonId] attribute is present.
    /// </summary>
    [Fact]
    public void ResolveId_WithBsonId_Works()
    {
        var prop = GlobalIdConventionProvider.Convention.ResolveIdProperty<WithBsonId>();
        Assert.NotNull(prop);
        Assert.Equal("CustomId", prop!.Name);
    }

    /// <summary>
    /// Verifies that the Id property is correctly resolved when the default "Id" name is used.
    /// </summary>
    [Fact]
    public void ResolveId_DefaultIdName_Works()
    {
        var prop = GlobalIdConventionProvider.Convention.ResolveIdProperty<WithDefaultId>();
        Assert.NotNull(prop);
        Assert.Equal("Id", prop!.Name);
    }

    /// <summary>
    /// Verifies that no Id property is returned when the entity does not have one.
    /// </summary>
    [Fact]
    public void ResolveId_MissingProperty_ReturnsNull()
    {
        var prop = GlobalIdConventionProvider.Convention.ResolveIdProperty<WithoutId>();
        Assert.Null(prop);
    }

    /// <summary>
    /// Verifies that a custom Id convention can override the default one.
    /// </summary>
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
            // Reset to default convention
            EntityIdAccessor<WithBsonId>.SetConvention(GlobalIdConventionProvider.Convention);
        }
    }

    private class WithBsonId
    {
        [BsonId]
        public int CustomId { get; set; }
    }

    private class WithDefaultId
    {
        public Guid Id { get; set; }
    }

    private class WithoutId
    {
    }

    private class FakeConvention : IIdConvention
    {
        public PropertyInfo? ResolveIdProperty<TEntity>() => null;
    }
}
