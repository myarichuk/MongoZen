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
    public unsafe void Should_Inject_Initial_ETag_On_Insert()
    {
        var tracker = new ChangeTracker(new DocumentConventions(), _allocator);
        var entity = new ConcurrencyEntity { Id = 1, Name = "New" };
        
        tracker.Track(entity);
        
        using var tempArena = new ArenaAllocator(1024);
        var buffer = new PendingOperation[tracker.TrackedCount];
        var entities = new object[tracker.TrackedCount];
        var count = tracker.GetPendingUpdates(buffer, entities, tempArena);
        
        var insertOp = buffer[0];
        Assert.Equal(OperationType.Insert, insertOp.Type);
        
        // ETag should have been set on the entity
        Assert.NotEqual(Guid.Empty, entity.Version);
        
        var doc = ArenaBsonReader.Read(new ReadOnlySpan<byte>(insertOp.PayloadPtr, insertOp.PayloadLength), _allocator);
        Assert.True(doc.TryGetElementOffset("_etag", out _));
        Assert.Equal(entity.Version, doc.GetGuid("_etag"));
    }

    [Fact]
    public unsafe void Should_Include_ETag_In_Update_Filter()
    {
        var tracker = new ChangeTracker(new DocumentConventions(), _allocator);
        var initialETag = Guid.NewGuid();
        var entity = new ConcurrencyEntity { Id = 1, Name = "Original", Version = initialETag };
        
        // Create a snapshot that has the initial ETag
        using var tempArena = new ArenaAllocator(1024);
        var writer = new ArenaBsonWriter(tempArena);
        writer.WriteStartDocument();
        writer.WriteInt32("_id", 1);
        writer.WriteString("Name", "Original");
        writer.WriteGuid("_etag", initialETag);
        writer.WriteEndDocument();
        var snapshot = writer.Commit(tempArena);
        
        tracker.Track(entity, snapshot);
        
        entity.Name = "Updated";
        using var tempArena2 = new ArenaAllocator(1024);
        var buffer = new PendingOperation[tracker.TrackedCount];
        var entities = new object[tracker.TrackedCount];
        var count = tracker.GetPendingUpdates(buffer, entities, tempArena2);
        
        var updateOp = buffer[0];
        Assert.Equal(OperationType.Update, updateOp.Type);
        
        // ETag should have been updated on the entity
        Assert.NotEqual(initialETag, entity.Version);
        
        // Verify filter data in operation
        Assert.Equal(DocId.From(1), updateOp.Id);
        Assert.Equal(initialETag, updateOp.ExpectedEtag);
        
        // Verify update document includes new _etag
        var updateDoc = ArenaBsonReader.Read(new ReadOnlySpan<byte>(updateOp.PayloadPtr, updateOp.PayloadLength), _allocator);
        
        // Updates are encoded as $set: { ... }
        var setDoc = updateDoc.GetDocument("$set", _allocator);
        Assert.Equal("Updated", setDoc.GetString("Name"));
        Assert.Equal(entity.Version, setDoc.GetGuid("_etag"));
    }

    [Fact]
    public unsafe void Should_Handle_Hidden_ETag_Update()
    {
        var tracker = new ChangeTracker(new DocumentConventions(), _allocator);
        var initialETag = Guid.NewGuid();
        var entity = new HiddenConcurrencyEntity { Id = 1, Name = "Original" };
        
        // Create a snapshot that has the initial ETag
        using var tempArena = new ArenaAllocator(1024);
        var writer = new ArenaBsonWriter(tempArena);
        writer.WriteStartDocument();
        writer.WriteInt32("_id", 1);
        writer.WriteString("Name", "Original");
        writer.WriteGuid("_etag", initialETag);
        writer.WriteEndDocument();
        var snapshot = writer.Commit(tempArena);
        
        tracker.Track(entity, snapshot);
        
        entity.Name = "Updated";
        using var tempArena2 = new ArenaAllocator(1024);
        var buffer = new PendingOperation[tracker.TrackedCount];
        var entities = new object[tracker.TrackedCount];
        var count = tracker.GetPendingUpdates(buffer, entities, tempArena2);
        
        var updateOp = buffer[0];
        Assert.Equal(OperationType.Update, updateOp.Type);
        
        // Verify filter includes _id AND expected _etag
        Assert.Equal(DocId.From(1), updateOp.Id);
        Assert.Equal(initialETag, updateOp.ExpectedEtag);
        
        // Verify update document includes a NEW _etag
        var updateDoc = ArenaBsonReader.Read(new ReadOnlySpan<byte>(updateOp.PayloadPtr, updateOp.PayloadLength), _allocator);
        var setDoc = updateDoc.GetDocument("$set", _allocator);
        Assert.Equal("Updated", setDoc.GetString("Name"));
        Assert.True(setDoc.TryGetElementOffset("_etag", out _));
        Assert.NotEqual(initialETag, setDoc.GetGuid("_etag"));
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
    public unsafe void Should_Not_Update_If_No_Changes_Detected()
    {
        var tracker = new ChangeTracker(new DocumentConventions(), _allocator);
        var initialETag = Guid.NewGuid();
        var entity = new ConcurrencyEntity { Id = 1, Name = "Original", Version = initialETag };
        
        // Create a snapshot that has the initial ETag
        using var tempArena = new ArenaAllocator(1024);
        var writer = new ArenaBsonWriter(tempArena);
        writer.WriteStartDocument();
        writer.WriteInt32("_id", 1);
        writer.WriteString("Name", "Original");
        writer.WriteGuid("_etag", initialETag);
        writer.WriteEndDocument();
        var snapshot = writer.Commit(tempArena);
        
        tracker.Track(entity, snapshot);
        
        // No changes to entity
        using var tempArena2 = new ArenaAllocator(1024);
        var buffer = new PendingOperation[tracker.TrackedCount];
        var entities = new object[tracker.TrackedCount];
        var count = tracker.GetPendingUpdates(buffer, entities, tempArena2);
        
        // No updates should be generated
        Assert.Equal(0, count);
        
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
