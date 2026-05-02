using System.Collections.Concurrent;
using SharpArena.Allocators;
using MongoDB.Driver;
using MongoDB.Bson;
using MongoZen.Bson;

namespace MongoZen;

public sealed class ChangeTracker(ArenaAllocator arena)
{
    private ArenaAllocator _arena = arena;
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

    public void RefreshSnapshots(ArenaAllocator newArena)
    {
        foreach (var entry in _trackedEntities.Values)
        {
            if (entry.IsDeleted)
            {
                _trackedEntities.TryRemove(entry.Entity, out _);
                continue;
            }

            var writer = new ArenaBsonWriter(newArena);
            entry.UpdateSnapshot(ref writer, newArena);
            entry.IsNew = false;
        }
        _arena = newArena;
    }

    public Dictionary<string, List<IPendingUpdate>> GetGroupedUpdates()
    {
        var groups = new Dictionary<string, List<IPendingUpdate>>();
        var builder = Builders<BsonDocument>.Update;

        foreach (var entry in _trackedEntities.Values)
        {
            var collectionName = DocumentTypeTracker.GetDefaultCollectionName(entry.Type);
            if (!groups.TryGetValue(collectionName, out var updates))
            {
                updates = new List<IPendingUpdate>();
                groups[collectionName] = updates;
            }

            if (entry.IsDeleted)
            {
                var id = EntityIdAccessor.GetId(entry.Entity);
                updates.Add(new DeleteOperation(id!, collectionName));
                continue;
            }

            if (entry.IsNew)
            {
                var operationType = typeof(InsertOperation<>).MakeGenericType(entry.Type);
                updates.Add((IPendingUpdate)Activator.CreateInstance(operationType, [entry.Entity, collectionName])!);
                continue;
            }

            var update = entry.BuildUpdate(builder);
            if (update != null)
            {
                var id = EntityIdAccessor.GetId(entry.Entity);
                updates.Add(new UpdateOperation<BsonDocument>(id!, update, collectionName));
            }
        }
        return groups;
    }

    private class EntityEntry
    {
        public object Entity { get; init; } = null!;
        public Type Type { get; init; } = null!;
        public BlittableBsonDocument? Snapshot { get; set; }
        public bool IsNew { get; set; }
        public bool IsDeleted { get; set; }
        
        private Delegate? _cachedBuildUpdate;
        private Delegate? _cachedSerialize;

        public void UpdateSnapshot(ref ArenaBsonWriter writer, ArenaAllocator arena)
        {
            _cachedSerialize ??= (Delegate)typeof(DynamicBlittableSerializer<>)
                .MakeGenericType(Type)
                .GetField(nameof(DynamicBlittableSerializer<object>.SerializeDelegate))!
                .GetValue(null)!;

            typeof(EntityEntry).GetMethod(nameof(UpdateSnapshotInternal), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .MakeGenericMethod(Type)
                .Invoke(this, [writer, arena]);
        }

        private void UpdateSnapshotInternal<T>(ArenaBsonWriter writer, ArenaAllocator arena)
        {
            var entity = (T)Entity;
            DynamicBlittableSerializer<T>.SerializeDelegate(ref writer, entity);
            Snapshot = writer.Commit(arena);
        }

        public UpdateDefinition<BsonDocument>? BuildUpdate(UpdateDefinitionBuilder<BsonDocument> builder)
        {
            if (Snapshot == null) return null;

            _cachedBuildUpdate ??= (Delegate)typeof(DynamicBlittableSerializer<>)
                .MakeGenericType(Type)
                .GetField(nameof(DynamicBlittableSerializer<object>.BuildUpdateDelegate))!
                .GetValue(null)!;

            return (UpdateDefinition<BsonDocument>?)_cachedBuildUpdate.DynamicInvoke(Entity, Snapshot.Value, builder);
        }
    }
}

public interface IPendingUpdate
{
    string CollectionName { get; }
    WriteModel<BsonDocument> ToWriteModel();
    Task ExecuteAsync(IMongoDatabase database, IClientSessionHandle? session, CancellationToken ct);
}

public sealed class InsertOperation<T>(T entity, string collectionName) : IPendingUpdate
{
    public string CollectionName => collectionName;

    public WriteModel<BsonDocument> ToWriteModel()
    {
        return new InsertOneModel<BsonDocument>(entity.ToBsonDocument());
    }

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
    public string CollectionName => collectionName;

    public WriteModel<BsonDocument> ToWriteModel()
    {
        return new UpdateOneModel<BsonDocument>(Builders<BsonDocument>.Filter.Eq("_id", id), update);
    }

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
    public string CollectionName => collectionName;
    public object Id => id;

    public WriteModel<BsonDocument> ToWriteModel()
    {
        return new DeleteOneModel<BsonDocument>(Builders<BsonDocument>.Filter.Eq("_id", id));
    }

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
