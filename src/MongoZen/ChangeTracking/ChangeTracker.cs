using System.Collections.Concurrent;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoZen.Bson;
using SharpArena.Allocators;

namespace MongoZen.ChangeTracking;

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

            var update = entry.BuildUpdate(builder, _arena);
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
        
        private IEntityDispatcher? _dispatcher;

        public void UpdateSnapshot(ref ArenaBsonWriter writer, ArenaAllocator arena)
        {
            GetDispatcher().UpdateSnapshot(this, ref writer, arena);
        }

        public UpdateDefinition<BsonDocument>? BuildUpdate(UpdateDefinitionBuilder<BsonDocument> builder, ArenaAllocator arena)
        {
            if (Snapshot == null) return null;
            return GetDispatcher().BuildUpdate(this, builder, arena);
        }

        private IEntityDispatcher GetDispatcher()
        {
            return _dispatcher ??= EntityDispatcherCache.Get(Type);
        }

        private interface IEntityDispatcher
        {
            void UpdateSnapshot(EntityEntry entry, ref ArenaBsonWriter writer, ArenaAllocator arena);
            UpdateDefinition<BsonDocument>? BuildUpdate(EntityEntry entry, UpdateDefinitionBuilder<BsonDocument> builder, ArenaAllocator arena);
        }

        private class EntityDispatcher<T> : IEntityDispatcher
        {
            public void UpdateSnapshot(EntityEntry entry, ref ArenaBsonWriter writer, ArenaAllocator arena)
            {
                var entity = (T)entry.Entity;
                DynamicBlittableSerializer<T>.SerializeDelegate(ref writer, entity);
                entry.Snapshot = writer.Commit(arena);
            }

            public UpdateDefinition<BsonDocument>? BuildUpdate(EntityEntry entry, UpdateDefinitionBuilder<BsonDocument> builder, ArenaAllocator arena)
            {
                var entity = (T)entry.Entity;
                return DynamicBlittableSerializer<T>.BuildUpdateDelegate(entity, entry.Snapshot!.Value, builder, arena);
            }
        }

        private static class EntityDispatcherCache
        {
            private static readonly ConcurrentDictionary<Type, IEntityDispatcher> _cache = new();
            public static IEntityDispatcher Get(Type type) => _cache.GetOrAdd(type, t => 
                (IEntityDispatcher)Activator.CreateInstance(typeof(EntityDispatcher<>).MakeGenericType(t))!);
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
