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
    Task ExecuteAsync(IMongoDatabase database, CancellationToken ct);
}

public sealed class InsertOperation<T>(T entity, string collectionName) : IPendingUpdate
{
    public async Task ExecuteAsync(IMongoDatabase database, CancellationToken ct)
    {
        var collection = database.GetCollection<T>(collectionName);
        await collection.InsertOneAsync(entity, cancellationToken: ct);
    }
}
