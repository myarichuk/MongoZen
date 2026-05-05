using MongoDB.Bson;
using MongoZen.Bson;
using MongoZen.ChangeTracking;
using SharpArena.Allocators;
using Xunit;

namespace MongoZen.Tests;

public class TrackingTests
{
    private readonly ArenaAllocator _allocator = new(1024 * 1024);
    
    private BlittableBsonDocument Snapshot<T>(T entity)
    {
        var writer = new ArenaBsonWriter(_allocator);
        DynamicBlittableSerializer<T>.SerializeDelegate(ref writer, entity);
        return writer.Commit(_allocator);
    }

    [Fact]
    public void Should_Detect_Simple_Change()
    {
        var entity = new SimpleTrackingEntity { Id = 1, Name = "Old" };
        var snapshot = Snapshot(entity);

        entity.Name = "New";
        var builder = new ArenaUpdateDefinitionBuilder(_allocator);
        SimpleTrackingEntity.BuildUpdate(entity, snapshot, ref builder, _allocator, default);

        Assert.True(builder.HasChanges);
        var doc = builder.Build();
        var setDoc = doc.GetDocument("$set".AsSpan(), _allocator);
        Assert.Equal("New", setDoc.GetString("Name".AsSpan()));
    }

    [Fact]
    public void Should_Respect_BsonIgnore()
    {
        var entity = new IgnoreEntity { Name = "Test", Computed = "Old" };
        var snapshot = Snapshot(entity);

        entity.Computed = "New";
        var builder = new ArenaUpdateDefinitionBuilder(_allocator);
        IgnoreEntity.BuildUpdate(entity, snapshot, ref builder, _allocator, default);

        // Computed is ignored, so no update should be generated
        Assert.False(builder.HasChanges);

        entity.Name = "Changed";
        builder = new ArenaUpdateDefinitionBuilder(_allocator);
        IgnoreEntity.BuildUpdate(entity, snapshot, ref builder, _allocator, default);
        Assert.True(builder.HasChanges);
    }

    [Fact]
    public void Should_Handle_Nullable_Primitives()
    {
        var entity = new NullableEntity { Id = 1, Age = 20 };
        var snapshot = Snapshot(entity);

        entity.Age = null;
        var builder = new ArenaUpdateDefinitionBuilder(_allocator);
        NullableEntity.BuildUpdate(entity, snapshot, ref builder, _allocator, default);
        
        Assert.True(builder.HasChanges);
        var doc = builder.Build();
        var setDoc = doc.GetDocument("$set".AsSpan(), _allocator);
        Assert.True(setDoc.ContainsKey("Age".AsSpan()));
    }

    [Fact]
    public void Should_Handle_Enums()
    {
        var entity = new EnumEntity { Status = Status.Active };
        var snapshot = Snapshot(entity);

        entity.Status = Status.Inactive;
        var builder = new ArenaUpdateDefinitionBuilder(_allocator);
        EnumEntity.BuildUpdate(entity, snapshot, ref builder, _allocator, default);

        Assert.True(builder.HasChanges);
        var doc = builder.Build();
        var setDoc = doc.GetDocument("$set".AsSpan(), _allocator);
        Assert.Equal((int)Status.Inactive, setDoc.GetInt32("Status".AsSpan()));
    }

    [Fact]
    public void Should_Support_Deep_Diffing()
    {
        var entity = new ComplexEntity 
        { 
            Id = 1, 
            Child = new SimpleTrackingEntity { Name = "Child" } 
        };
        var snapshot = Snapshot(entity);

        entity.Child.Name = "Updated Child";
        var builder = new ArenaUpdateDefinitionBuilder(_allocator);
        ComplexEntity.BuildUpdate(entity, snapshot, ref builder, _allocator, default);

        Assert.True(builder.HasChanges);
        var doc = builder.Build();
        var setDoc = doc.GetDocument("$set".AsSpan(), _allocator);
        Assert.Equal("Updated Child", setDoc.GetString("Child.Name".AsSpan()));
    }
}

[Document]
public partial class SimpleTrackingEntity
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
}

[Document]
public partial class IgnoreEntity
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    
    [MongoDB.Bson.Serialization.Attributes.BsonIgnore]
    public string Computed { get; set; } = "";
}

[Document]
public partial class NullableEntity
{
    public int Id { get; set; }
    public int? Age { get; set; }
}

public enum Status { Active, Inactive }

[Document]
public partial class EnumEntity
{
    public int Id { get; set; }
    public Status Status { get; set; }
}

[Document]
public partial class ComplexEntity
{
    public int Id { get; set; }
    public SimpleTrackingEntity Child { get; set; } = null!;
}
