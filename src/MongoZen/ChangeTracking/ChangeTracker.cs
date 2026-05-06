using System.Buffers;
using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Reflection;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoZen.Bson;
using SharpArena.Allocators;
using SharpArena.Collections;

namespace MongoZen.ChangeTracking;

public sealed class ChangeTracker(DocumentConventions conventions, ArenaAllocator arena)
{
    private ArenaAllocator _arena = arena;
    private readonly ConcurrentDictionary<object, EntityEntry> _trackedEntities = new();

    public int TrackedCount => _trackedEntities.Count;

    public void Track<T>(T entity, BlittableBsonDocument? snapshot = null)
    // ... rest of Track method ...
    {
        if (entity == null)
        {
            throw new ArgumentNullException(nameof(entity));
        }

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
        if (entity == null)
        {
            throw new ArgumentNullException(nameof(entity));
        }

        _trackedEntities[entity] = new EntityEntry
        {
            Entity = entity,
            Type = entity.GetType(),
            ExpectedETag = expectedEtag,
            IsNew = false
        };
    }

    public void Evict(object? entity)
    {
        if (entity == null)
        {
            return;
        }

        _trackedEntities.TryRemove(entity, out _);
    }

    public Guid? GetExpectedETag(object? entity)
    {
        if (entity == null)
        {
            return null;
        }

        return _trackedEntities.TryGetValue(entity, out var entry) ? entry.ExpectedETag : null;
    }

    public void TrackDelete<T>(T entity)
    {
        if (entity == null)
        {
            throw new ArgumentNullException(nameof(entity));
        }

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

    public int GetPendingUpdates(PendingOperation[] buffer, ArenaAllocator tempArena)
    {
        var count = 0;
        var pathBuffer = ArrayPool<char>.Shared.Rent(256);

        try
        {
            foreach (var entry in _trackedEntities.Values)
            {
                if (entry is { IsDeleted: true, IsNew: true })
                {
                    continue;
                }

                var collectionName = conventions.GetCollectionName(entry.Type);

                if (entry.IsDeleted)
                {
                    var id = EntityIdAccessor.GetId(entry.Entity);
                    buffer[count++] = new PendingOperation
                    {
                        Type = OperationType.Delete,
                        CollectionName = collectionName,
                        Id = id!,
                        ExpectedEtag = entry.ExpectedETag ?? Guid.Empty,
                        Entity = entry.Entity
                    };
                }
                else if (entry.IsNew)
                {
                    var newEtag = Guid.NewGuid();
                    if (entry.HasConcurrencyCheck)
                    {
                        entry.SetNewETag(newEtag);
                    }

                    var writer = new ArenaBsonWriter(_arena);
                    entry.UpdateSnapshot(ref writer, _arena);

                    buffer[count++] = new PendingOperation
                    {
                        Type = OperationType.Insert,
                        CollectionName = collectionName,
                        Document = entry.Snapshot!.Value,
                        Entity = entry.Entity
                    };
                }
                else
                {
                    var builder = new ArenaUpdateDefinitionBuilder(_arena, pathBuffer);
                    entry.BuildUpdate(ref builder, _arena, default);

                    if (builder.HasChanges)
                    {
                        var nextEtag = Guid.NewGuid();
                        if (entry.HasConcurrencyCheck)
                        {
                            entry.SetNewETag(nextEtag);
                        }

                        builder.Set("_etag", nextEtag);

                        buffer[count++] = new PendingOperation
                        {
                            Type = OperationType.Update,
                            CollectionName = collectionName,
                            Id = EntityIdAccessor.GetId(entry.Entity)!,
                            ExpectedEtag = entry.ExpectedETag ?? Guid.Empty,
                            Document = builder.Build(),
                            Entity = entry.Entity
                        };
                    }
                }
            }
        }
        finally
        {
            ArrayPool<char>.Shared.Return(pathBuffer);
        }
        return count;
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

        public void UpdateSnapshot(ref ArenaBsonWriter writer, ArenaAllocator arena) => 
            GetDispatcher().UpdateSnapshot(this, ref writer, arena);

        public void BuildUpdate(ref ArenaUpdateDefinitionBuilder builder, ArenaAllocator arena, ReadOnlySpan<char> pathPrefix)
        {
            if (Snapshot == null)
            {
                return;
            }

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
                
                if (prop == null || prop.PropertyType != typeof(Guid) || !prop.CanWrite)
                {
                    return null;
                }

                var entityParam = Expression.Parameter(typeof(T), "entity");
                var etagParam = Expression.Parameter(typeof(Guid), "etag");
                var assign = Expression.Assign(Expression.Property(entityParam, prop), etagParam);
                return Expression.Lambda<Action<T, Guid>>(assign, entityParam, etagParam).Compile();
            }

            public bool HasConcurrencyCheck => ETagSetter != null;

            public void SetETag(object entity, Guid etag) => 
                ETagSetter?.Invoke((T)entity, etag);

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
            private static readonly ConcurrentDictionary<Type, IEntityDispatcher> Cache = new();
            public static IEntityDispatcher Get(Type type) => Cache.GetOrAdd(type, t => 
                (IEntityDispatcher)Activator.CreateInstance(typeof(EntityDispatcher<>).MakeGenericType(t))!);
        }
    }
}

public enum OperationType : byte
{
    Insert,
    Update,
    Delete
}

public readonly struct PendingOperation
{
    public OperationType Type { get; init; }
    public string CollectionName { get; init; }
    public object Id { get; init; }
    public Guid ExpectedEtag { get; init; }
    public BlittableBsonDocument? Document { get; init; }
    public object Entity { get; init; }

    public WriteModel<BsonDocument> ToWriteModel(DocumentConventions conventions)
    {
        switch (Type)
        {
            case OperationType.Insert:
                var rawInsert = new RawBsonDocument(Document!.Value.AsReadOnlySpan().ToArray());
                return new InsertOneModel<BsonDocument>(new BsonDocument(rawInsert));

            case OperationType.Update:
                var filterUpdate = Builders<BsonDocument>.Filter.Eq("_id", conventions.CreateBsonValue(Id));
                if (ExpectedEtag != Guid.Empty)
                {
                    filterUpdate = Builders<BsonDocument>.Filter.And(filterUpdate, Builders<BsonDocument>.Filter.Eq("_etag", conventions.CreateBsonValue(ExpectedEtag)));
                }
                var rawUpdate = new RawBsonDocument(Document!.Value.AsReadOnlySpan().ToArray());
                return new UpdateOneModel<BsonDocument>(filterUpdate, rawUpdate);

            case OperationType.Delete:
                var filterDelete = Builders<BsonDocument>.Filter.Eq("_id", conventions.CreateBsonValue(Id));
                if (ExpectedEtag != Guid.Empty)
                {
                    filterDelete = Builders<BsonDocument>.Filter.And(filterDelete, Builders<BsonDocument>.Filter.Eq("_etag", conventions.CreateBsonValue(ExpectedEtag)));
                }
                return new DeleteOneModel<BsonDocument>(filterDelete);

            default:
                throw new InvalidOperationException("Unknown operation type.");
        }
    }
}
