using System.Collections.Concurrent;
using SharpArena.Allocators;
using MongoDB.Driver;
using MongoDB.Bson;
using MongoZen.Bson;

namespace MongoZen;

public sealed class ChangeTracker(ArenaAllocator arena)
{
    private readonly ArenaAllocator _arena = arena;
    private readonly ConcurrentDictionary<object, EntityEntry> _trackedEntities = new();

    public void Track<T>(T entity, BlittableBsonDocument? snapshot = null)
    {
        if (entity == null) throw new ArgumentNullException(nameof(entity));

        var entry = new EntityEntry
        {
            Entity = entity,
            Type = typeof(T),
            Snapshot = snapshot,
            IsNew = snapshot == null
        };

        _trackedEntities[entity] = entry;
    }

    public void TrackDelete<T>(T entity)
    {
        if (entity == null) throw new ArgumentNullException(nameof(entity));

        if (_trackedEntities.TryGetValue(entity, out var entry))
        {
            entry.IsDeleted = true;
        }
        else
        {
            _trackedEntities[entity] = new EntityEntry
            {
                Entity = entity,
                Type = typeof(T),
                IsDeleted = true
            };
        }
    }

    private BlittableBsonDocument CreateSnapshot<T>(T entity)
    {
        var writer = new ArenaBsonWriter(_arena);
        DynamicBlittableSerializer<T>.SerializeDelegate(ref writer, entity);
        return writer.Commit(_arena);
    }

    public IEnumerable<IPendingUpdate> GetUpdates()
    {
        var updates = new List<IPendingUpdate>();
        var builder = Builders<BsonDocument>.Update;

        foreach (var entry in _trackedEntities.Values)
        {
            if (entry.IsDeleted)
            {
                var id = EntityIdAccessor.GetId(entry.Entity);
                var collectionName = DocumentTypeTracker.GetDefaultCollectionName(entry.Type);
                updates.Add(new DeleteOperation(id!, collectionName));
                continue;
            }

            if (entry.IsNew)
            {
                var collectionName = DocumentTypeTracker.GetDefaultCollectionName(entry.Type);
                var operationType = typeof(InsertOperation<>).MakeGenericType(entry.Type);
                updates.Add((IPendingUpdate)Activator.CreateInstance(operationType, [entry.Entity, collectionName])!);
                continue;
            }

            var update = entry.BuildUpdate(builder);
            if (update != null)
            {
                var id = EntityIdAccessor.GetId(entry.Entity);
                var collectionName = DocumentTypeTracker.GetDefaultCollectionName(entry.Type);
                updates.Add(new UpdateOperation<BsonDocument>(id!, update, collectionName));
            }
        }
        return updates;
    }

    private class EntityEntry
    {
        public object Entity { get; init; } = null!;
        public Type Type { get; init; } = null!;
        public BlittableBsonDocument? Snapshot { get; set; }
        public bool IsNew { get; init; }
        public bool IsDeleted { get; set; }
        
        private Delegate? _cachedDelegate;

        public UpdateDefinition<BsonDocument>? BuildUpdate(UpdateDefinitionBuilder<BsonDocument> builder)
        {
            if (Snapshot == null) return null;

            _cachedDelegate ??= (Delegate)typeof(DynamicBlittableSerializer<>)
                .MakeGenericType(Type)
                .GetField(nameof(DynamicBlittableSerializer<object>.BuildUpdateDelegate))!
                .GetValue(null)!;

            return (UpdateDefinition<BsonDocument>?)_cachedDelegate.DynamicInvoke(Entity, Snapshot.Value, builder);
        }
    }
}

public interface IPendingUpdate
{
    Task ExecuteAsync(IMongoDatabase database, IClientSessionHandle? session, CancellationToken ct);
}

public sealed class InsertOperation<T>(T entity, string collectionName) : IPendingUpdate
{
    public async Task ExecuteAsync(IMongoDatabase database, IClientSessionHandle? session, CancellationToken ct)
    {
        var collection = database.GetCollection<T>(collectionName);
        if (session != null)
            await collection.InsertOneAsync(session, entity, cancellationToken: ct);
        else
            await collection.InsertOneAsync(entity, cancellationToken: ct);
    }
}

public sealed class UpdateOperation<T>(object id, UpdateDefinition<BsonDocument> update, string collectionName) : IPendingUpdate
{
    public async Task ExecuteAsync(IMongoDatabase database, IClientSessionHandle? session, CancellationToken ct)
    {
        var collection = database.GetCollection<BsonDocument>(collectionName);
        var filter = Builders<BsonDocument>.Filter.Eq("_id", id);
        
        if (session != null)
            await collection.UpdateOneAsync(session, filter, update, cancellationToken: ct);
        else
            await collection.UpdateOneAsync(filter, update, cancellationToken: ct);
    }
}

public sealed class DeleteOperation(object id, string collectionName) : IPendingUpdate
{
    public object Id => id;

    public async Task ExecuteAsync(IMongoDatabase database, IClientSessionHandle? session, CancellationToken ct)
    {
        var collection = database.GetCollection<BsonDocument>(collectionName);
        var filter = Builders<BsonDocument>.Filter.Eq("_id", id);
        
        if (session != null)
            await collection.DeleteOneAsync(session, filter, cancellationToken: ct);
        else
            await collection.DeleteOneAsync(filter, cancellationToken: ct);
    }
}
