using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Reflection;
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

        if (snapshot.HasValue && snapshot.Value.TryGetElementOffset("_etag", out _))
        {
            entry.ExpectedETag = snapshot.Value.GetGuid("_etag");
        }

        _trackedEntities[entity] = entry;
    }

    public void Track(object entity, Guid expectedEtag)
    {
        if (entity == null) throw new ArgumentNullException(nameof(entity));

        _trackedEntities[entity] = new EntityEntry
        {
            Entity = entity,
            Type = entity.GetType(),
            ExpectedETag = expectedEtag,
            IsNew = false
        };
    }

    public void Evict(object entity)
    {
        if (entity == null) return;
        _trackedEntities.TryRemove(entity, out _);
    }

    public Guid? GetExpectedETag(object entity)
    {
        if (entity == null) return null;
        return _trackedEntities.TryGetValue(entity, out var entry) ? entry.ExpectedETag : null;
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
            
            // Refresh ExpectedETag from the new snapshot
            if (entry.Snapshot.HasValue && entry.Snapshot.Value.TryGetElementOffset("_etag", out _))
            {
                entry.ExpectedETag = entry.Snapshot.Value.GetGuid("_etag");
            }
        }
        _arena = newArena;
    }

    public BlittableBsonDocument? GetSnapshot(object entity)
    {
        return _trackedEntities.TryGetValue(entity, out var entry) ? entry.Snapshot : null;
    }

    public Dictionary<string, List<IPendingUpdate>> GetGroupedUpdates()
    {
        var groups = new Dictionary<string, List<IPendingUpdate>>();

        foreach (var entry in _trackedEntities.Values)
        {
            if (entry.IsDeleted && entry.IsNew)
            {
                // Entity was added and deleted in the same session without being persisted.
                continue;
            }

            var collectionName = DocumentTypeTracker.GetDefaultCollectionName(entry.Type);
            IPendingUpdate? update = null;

            if (entry.IsDeleted)
            {
                var id = EntityIdAccessor.GetId(entry.Entity);
                update = new DeleteOperation(id!, entry.ExpectedETag ?? Guid.Empty, collectionName);
            }
            else if (entry.IsNew)
            {
                var newEtag = Guid.NewGuid();
                if (entry.HasConcurrencyCheck) entry.SetNewETag(newEtag);
                
                var operationType = typeof(InsertOperation<>).MakeGenericType(entry.Type);
                update = (IPendingUpdate)Activator.CreateInstance(operationType, [entry.Entity, newEtag, collectionName])!;
            }
            else
            {
                var builder = new ArenaUpdateDefinitionBuilder(_arena);
                entry.BuildUpdate(ref builder, _arena, default);
                
                if (builder.HasChanges)
                {
                    var nextEtag = Guid.NewGuid();
                    // If we have concurrency check, generate a new ETag and set it on the POCO
                    // so it's included in the update.
                    if (entry.HasConcurrencyCheck)
                    {
                        entry.SetNewETag(nextEtag);
                    }
                    
                    // Always add the ETag update to the builder if we have changes
                    builder.Set("_etag", nextEtag);

                    var updateDoc = builder.Build();
                    var id = EntityIdAccessor.GetId(entry.Entity);
                    update = new UpdateOperation(id!, entry.ExpectedETag ?? Guid.Empty, updateDoc, collectionName);
                }
            }

            if (update != null)
            {
                if (!groups.TryGetValue(collectionName, out var updates))
                {
                    updates = new List<IPendingUpdate>();
                    groups[collectionName] = updates;
                }
                updates.Add(update);
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
        public Guid? ExpectedETag { get; set; }
        
        private IEntityDispatcher? _dispatcher;

        public bool HasConcurrencyCheck => GetDispatcher().HasConcurrencyCheck;

        public void SetNewETag(Guid etag) => GetDispatcher().SetETag(Entity, etag);

        public void UpdateSnapshot(ref ArenaBsonWriter writer, ArenaAllocator arena)
        {
            GetDispatcher().UpdateSnapshot(this, ref writer, arena);
        }

        public void BuildUpdate(ref ArenaUpdateDefinitionBuilder builder, ArenaAllocator arena, ReadOnlySpan<char> pathPrefix)
        {
            if (Snapshot == null) return;
            GetDispatcher().BuildUpdate(this, ref builder, arena, pathPrefix);
        }

        private IEntityDispatcher GetDispatcher()
        {
            return _dispatcher ??= EntityDispatcherCache.Get(Type);
        }

        private interface IEntityDispatcher
        {
            bool HasConcurrencyCheck { get; }
            void SetETag(object entity, Guid etag);
            void UpdateSnapshot(EntityEntry entry, ref ArenaBsonWriter writer, ArenaAllocator arena);
            void BuildUpdate(EntityEntry entry, ref ArenaUpdateDefinitionBuilder builder, ArenaAllocator arena, ReadOnlySpan<char> pathPrefix);
        }

        private class EntityDispatcher<T> : IEntityDispatcher
        {
            private static readonly Action<T, Guid>? ETagSetter = CompileETagSetter();

            private static Action<T, Guid>? CompileETagSetter()
            {
                var prop = typeof(T).GetProperties()
                    .FirstOrDefault(p => p.GetCustomAttribute<ConcurrencyCheckAttribute>() != null);
                
                if (prop == null || prop.PropertyType != typeof(Guid) || !prop.CanWrite) return null;

                var entityParam = Expression.Parameter(typeof(T), "entity");
                var etagParam = Expression.Parameter(typeof(Guid), "etag");
                var assign = Expression.Assign(Expression.Property(entityParam, prop), etagParam);
                return Expression.Lambda<Action<T, Guid>>(assign, entityParam, etagParam).Compile();
            }

            public bool HasConcurrencyCheck => ETagSetter != null;

            public void SetETag(object entity, Guid etag)
            {
                ETagSetter?.Invoke((T)entity, etag);
            }

            public void UpdateSnapshot(EntityEntry entry, ref ArenaBsonWriter writer, ArenaAllocator arena)
            {
                var entity = (T)entry.Entity;
                DynamicBlittableSerializer<T>.SerializeDelegate(ref writer, entity);
                entry.Snapshot = writer.Commit(arena);
            }

            public void BuildUpdate(EntityEntry entry, ref ArenaUpdateDefinitionBuilder builder, ArenaAllocator arena, ReadOnlySpan<char> pathPrefix)
            {
                var entity = (T)entry.Entity;
                DynamicBlittableSerializer<T>.BuildUpdateDelegate(entity, entry.Snapshot!.Value, ref builder, arena, pathPrefix);
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

public sealed class InsertOperation<T>(T entity, Guid etag, string collectionName) : IPendingUpdate
{
    public string CollectionName => collectionName;

    public WriteModel<BsonDocument> ToWriteModel()
    {
        using var arena = new ArenaAllocator(1024);
        var writer = new ArenaBsonWriter(arena);
        DynamicBlittableSerializer<T>.SerializeDelegate(ref writer, entity);
        var doc = writer.Commit(arena);
        
        var raw = new RawBsonDocument(doc.AsReadOnlySpan().ToArray());
        if (!raw.Contains("_etag"))
        {
            var docWithEtag = raw.ToBsonDocument();
            docWithEtag["_etag"] = BsonValue.Create(etag);
            return new InsertOneModel<BsonDocument>(docWithEtag);
        }
        
        return new InsertOneModel<BsonDocument>(raw);
    }

    public async Task ExecuteAsync(IMongoDatabase database, IClientSessionHandle? session, CancellationToken ct)
    {
        var collection = database.GetCollection<BsonDocument>(collectionName);
        var model = ToWriteModel();
        var insertModel = (InsertOneModel<BsonDocument>)model;

        if (session != null)
            await collection.InsertOneAsync(session, insertModel.Document, cancellationToken: ct);
        else
            await collection.InsertOneAsync(insertModel.Document, cancellationToken: ct);
    }
}

public sealed class UpdateOperation(object id, Guid expectedEtag, BlittableBsonDocument update, string collectionName) : IPendingUpdate
{
    public string CollectionName => collectionName;

    public WriteModel<BsonDocument> ToWriteModel()
    {
        var filter = Builders<BsonDocument>.Filter.And(
            Builders<BsonDocument>.Filter.Eq("_id", id),
            Builders<BsonDocument>.Filter.Eq("_etag", expectedEtag)
        );
        var updateDoc = new RawBsonDocument(update.AsReadOnlySpan().ToArray());
        return new UpdateOneModel<BsonDocument>(filter, updateDoc);
    }

    public async Task ExecuteAsync(IMongoDatabase database, IClientSessionHandle? session, CancellationToken ct)
    {
        var collection = database.GetCollection<BsonDocument>(collectionName);
        var model = ToWriteModel();
        var updateModel = (UpdateOneModel<BsonDocument>)model;
        
        if (session != null)
            await collection.UpdateOneAsync(session, updateModel.Filter, updateModel.Update, cancellationToken: ct);
        else
            await collection.UpdateOneAsync(updateModel.Filter, updateModel.Update, cancellationToken: ct);
    }
}

public sealed class DeleteOperation(object id, Guid expectedEtag, string collectionName) : IPendingUpdate
{
    public string CollectionName => collectionName;
    public object Id => id;

    public WriteModel<BsonDocument> ToWriteModel()
    {
        var filter = Builders<BsonDocument>.Filter.And(
            Builders<BsonDocument>.Filter.Eq("_id", id),
            Builders<BsonDocument>.Filter.Eq("_etag", expectedEtag)
        );
        return new DeleteOneModel<BsonDocument>(filter);
    }

    public async Task ExecuteAsync(IMongoDatabase database, IClientSessionHandle? session, CancellationToken ct)
    {
        var collection = database.GetCollection<BsonDocument>(collectionName);
        var model = ToWriteModel();
        var deleteModel = (DeleteOneModel<BsonDocument>)model;
        
        if (session != null)
            await collection.DeleteOneAsync(session, deleteModel.Filter, cancellationToken: ct);
        else
            await collection.DeleteOneAsync(deleteModel.Filter, cancellationToken: ct);
    }
}
