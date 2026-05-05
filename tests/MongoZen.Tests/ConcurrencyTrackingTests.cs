using System;
using System.Linq;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Serializers;
using MongoDB.Driver;
using MongoZen.Bson;
using MongoZen.ChangeTracking;
using SharpArena.Allocators;
using Xunit;

namespace MongoZen.Tests;

public class ConcurrencyTrackingTests
{
    static ConcurrencyTrackingTests()
    {
        try 
        { 
            BsonSerializer.RegisterSerializer(new GuidSerializer(GuidRepresentation.Standard)); 
        } 
        catch (BsonSerializationException) 
        { 
            // Already registered
        }
    }

    private readonly ArenaAllocator _allocator = new(1024 * 1024);

    [Fact]
    public void Should_Inject_Initial_ETag_On_Insert()
    {
        var tracker = new ChangeTracker(_allocator);
        var entity = new ConcurrencyEntity { Id = 1, Name = "New" };
        
        tracker.Track(entity);
        var groupedUpdates = tracker.GetGroupedUpdates();
        
        var updates = groupedUpdates.Values.First();
        var insertOp = updates.First();
        Assert.IsType<InsertOperation<ConcurrencyEntity>>(insertOp);
        
        // ETag should have been set on the entity
        Assert.NotEqual(Guid.Empty, entity.Version);
        
        var writeModel = insertOp.ToWriteModel();
        var insertModel = Assert.IsType<InsertOneModel<BsonDocument>>(writeModel);
        Assert.True(insertModel.Document.Contains("_etag"));
        Assert.Equal(entity.Version, insertModel.Document["_etag"].AsGuid);
    }

    [Fact]
    public void Should_Include_ETag_In_Update_Filter()
    {
        var tracker = new ChangeTracker(_allocator);
        var initialETag = Guid.NewGuid();
        var entity = new ConcurrencyEntity { Id = 1, Name = "Original", Version = initialETag };
        
        // Create a snapshot that has the initial ETag
        using var tempArena = new ArenaAllocator(1024);
        var writer = new ArenaBsonWriter(tempArena);
        writer.WriteStartDocument();
        writer.WriteInt32("_id", 1);
        writer.WriteString("Name", "Original".AsSpan());
        writer.WriteGuid("_etag", initialETag);
        writer.WriteEndDocument();
        var snapshot = writer.Commit(tempArena);
        
        tracker.Track(entity, snapshot);
        
        entity.Name = "Updated";
        var groupedUpdates = tracker.GetGroupedUpdates();
        
        var updates = groupedUpdates.Values.First();
        var updateOp = Assert.IsType<UpdateOperation>(updates.First());
        
        // ETag should have been updated on the entity
        Assert.NotEqual(initialETag, entity.Version);
        
        var writeModel = updateOp.ToWriteModel();
        var updateModel = Assert.IsType<UpdateOneModel<BsonDocument>>(writeModel);
        
        // Verify filter includes _id AND _etag
        var filter = updateModel.Filter.Render(new RenderArgs<BsonDocument>(BsonSerializer.LookupSerializer<BsonDocument>(), BsonSerializer.SerializerRegistry));
        Assert.Equal(1, filter["_id"].AsInt32);
        Assert.Equal(initialETag, filter["_etag"].AsGuid);
        
        // Verify update document includes new _etag
        var updateDoc = updateModel.Update.Render(new RenderArgs<BsonDocument>(BsonSerializer.LookupSerializer<BsonDocument>(), BsonSerializer.SerializerRegistry)).AsBsonDocument;
        Assert.Equal("Updated", updateDoc["$set"]["Name"].AsString);
        Assert.Equal(entity.Version, updateDoc["$set"]["_etag"].AsGuid);
    }

    [Fact]
    public void Should_Handle_Hidden_ETag_Update()
    {
        var tracker = new ChangeTracker(_allocator);
        var initialETag = Guid.NewGuid();
        var entity = new HiddenConcurrencyEntity { Id = 1, Name = "Original" };
        
        // Create a snapshot that has the initial ETag
        using var tempArena = new ArenaAllocator(1024);
        var writer = new ArenaBsonWriter(tempArena);
        writer.WriteStartDocument();
        writer.WriteInt32("_id", 1);
        writer.WriteString("Name", "Original".AsSpan());
        writer.WriteGuid("_etag", initialETag);
        writer.WriteEndDocument();
        var snapshot = writer.Commit(tempArena);
        
        tracker.Track(entity, snapshot);
        
        entity.Name = "Updated";
        var groupedUpdates = tracker.GetGroupedUpdates();
        
        var updates = groupedUpdates.Values.First();
        var updateOp = Assert.IsType<UpdateOperation>(updates.First());
        
        var writeModel = updateOp.ToWriteModel();
        var updateModel = Assert.IsType<UpdateOneModel<BsonDocument>>(writeModel);
        
        // Verify filter includes _id AND expected _etag
        var filter = updateModel.Filter.Render(new RenderArgs<BsonDocument>(BsonSerializer.LookupSerializer<BsonDocument>(), BsonSerializer.SerializerRegistry));
        Assert.Equal(1, filter["_id"].AsInt32);
        Assert.Equal(initialETag, filter["_etag"].AsGuid);
        
        // Verify update document includes a NEW _etag
        var updateDoc = updateModel.Update.Render(new RenderArgs<BsonDocument>(BsonSerializer.LookupSerializer<BsonDocument>(), BsonSerializer.SerializerRegistry)).AsBsonDocument;
        Assert.Equal("Updated", updateDoc["$set"]["Name"].AsString);
        Assert.True(updateDoc["$set"].AsBsonDocument.Contains("_etag"));
        Assert.NotEqual(initialETag, updateDoc["$set"]["_etag"].AsGuid);
    }

    [Fact]
    public void Should_Deserialize_ETag_Into_Property()
    {
        var expectedETag = Guid.NewGuid();
        using var tempArena = new ArenaAllocator(1024);
        var writer = new ArenaBsonWriter(tempArena);
        writer.WriteStartDocument();
        writer.WriteInt32("_id", 1);
        writer.WriteGuid("_etag", expectedETag);
        writer.WriteEndDocument();
        var snapshot = writer.Commit(tempArena);

        var entity = DynamicBlittableSerializer<ConcurrencyEntity>.DeserializeDelegate(snapshot, tempArena);
        
        Assert.Equal(expectedETag, entity.Version);
    }

    [Fact]
    public void Should_Not_Update_If_No_Changes_Detected()
    {
        var tracker = new ChangeTracker(_allocator);
        var initialETag = Guid.NewGuid();
        var entity = new ConcurrencyEntity { Id = 1, Name = "Original", Version = initialETag };
        
        // Create a snapshot that has the initial ETag
        using var tempArena = new ArenaAllocator(1024);
        var writer = new ArenaBsonWriter(tempArena);
        writer.WriteStartDocument();
        writer.WriteInt32("_id", 1);
        writer.WriteString("Name", "Original".AsSpan());
        writer.WriteGuid("_etag", initialETag);
        writer.WriteEndDocument();
        var snapshot = writer.Commit(tempArena);
        
        tracker.Track(entity, snapshot);
        
        // No changes to entity
        var groupedUpdates = tracker.GetGroupedUpdates();
        
        // No updates should be generated
        Assert.Empty(groupedUpdates);
        
        // ETag should NOT have been updated on the entity
        Assert.Equal(initialETag, entity.Version);
    }
}

[Document]
public partial class ConcurrencyEntity
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    
    [ConcurrencyCheck]
    public Guid Version { get; set; }
}

[Document]
public partial class HiddenConcurrencyEntity
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
}
