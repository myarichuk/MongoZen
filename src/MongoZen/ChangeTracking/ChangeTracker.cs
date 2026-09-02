using System.Buffers;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using MongoZen.Bson;
using SharpArena.Allocators;
using SharpArena.Collections;

namespace MongoZen.ChangeTracking;

public enum OperationType : byte
{
    Insert,
    Update,
    Delete
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct PendingOperation
{
    public OperationType Type;
    public int CollectionId;
    public DocId Id;
    public Guid ExpectedEtag;
    public byte* PayloadPtr;
    public int PayloadLength;
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct EntityEntry
{
    public DocId Id;
    public byte* BsonPtr;
    public int BsonLen;
    public Guid ExpectedETag;
    public int DispatcherId;
    public byte Flags; // 1 = IsNew, 2 = IsDeleted

    public bool IsNew
    {
        get => (Flags & 1) != 0;
        set => Flags = (byte)(value ? (Flags | 1) : (Flags & ~1));
    }

    public bool IsDeleted
    {
        get => (Flags & 2) != 0;
        set => Flags = (byte)(value ? (Flags | 2) : (Flags & ~2));
    }

    public BlittableBsonDocument GetSnapshot(ArenaAllocator arena)
    {
        if (BsonPtr == null) return default;
        return ArenaBsonReader.ReadInPlace(BsonPtr, BsonLen, arena);
    }

    public void SetSnapshot(BlittableBsonDocument doc)
    {
        BsonPtr = doc.Pointer;
        BsonLen = doc.Length;
    }
}

public unsafe struct ChangeTracker
{
    private ArenaAllocator _arena;
    private readonly DocumentConventions _conventions;
    private ArenaList<EntityEntry> _entries;
    private object[] _entities;
    private int _count;
    private readonly Dictionary<object, int> _entityIndex;   // reference → slot index
    private readonly Dictionary<DocId, int> _docIdIndex;     // DocId → slot index
    private readonly Dictionary<string, int> _collectionNameToId;
    private readonly List<string> _collectionNamesById;      // reverse: id → name

    public ChangeTracker(DocumentConventions conventions, ArenaAllocator arena)
    {
        _conventions = conventions;
        _arena = arena;
        _entries = new ArenaList<EntityEntry>(arena, 64);
        _entities = ArrayPool<object>.Shared.Rent(64);
        _count = 0;
        _entityIndex = new Dictionary<object, int>(ReferenceEqualityComparer.Instance);
        _docIdIndex = new Dictionary<DocId, int>();
        _collectionNameToId = new Dictionary<string, int>();
        _collectionNamesById = new List<string>();
    }

    public int TrackedCount => _count;

    public string GetCollectionName(int collectionId) => _collectionNamesById[collectionId];

    public int GetCollectionId(string name)
    {
        if (_collectionNameToId.TryGetValue(name, out var id)) return id;
        id = _collectionNamesById.Count;
        _collectionNamesById.Add(name);
        _collectionNameToId[name] = id;
        return id;
    }

    private void EnsureCapacity()
    {
        if (_count >= _entities.Length)
        {
            var newEntities = ArrayPool<object>.Shared.Rent(_entities.Length * 2);
            Array.Copy(_entities, newEntities, _count);
            ArrayPool<object>.Shared.Return(_entities);
            _entities = newEntities;
        }
    }

    public void Dispose()
    {
        if (_entities != null)
        {
            ArrayPool<object>.Shared.Return(_entities, clearArray: true);
            _entities = null!;
        }
    }

    private int FindIndex(object entity) =>
        _entityIndex.TryGetValue(entity, out var idx) ? idx : -1;

    private int FindIndex(DocId id) =>
        _docIdIndex.TryGetValue(id, out var idx) ? idx : -1;

    public void Track<T>(T entity, BlittableBsonDocument? snapshot = null)
    {
        if (entity == null) throw new ArgumentNullException(nameof(entity));

        var index = FindIndex(entity);
        if (index != -1)
        {
            ref var entry = ref _entries[index];
            if (snapshot.HasValue && !snapshot.Value.IsDefault)
            {
                entry.SetSnapshot(snapshot.Value);
                entry.IsNew = false;
                if (snapshot.Value.TryGetElementOffset("_etag", out _))
                {
                    entry.ExpectedETag = snapshot.Value.GetGuid("_etag");
                }
            }
            else
            {
                entry.BsonPtr = null;
                entry.BsonLen = 0;
                entry.IsNew = true;
            }
            return;
        }

        // Check by ID as well just in case, although DocumentSession should prevent this
        var docId = EntityIdAccessor.GetDocId(entity);
        index = FindIndex(docId);
        if (index != -1)
        {
            // Already tracking another object with same ID — update the entity reference.
            var oldEntity = _entities[index];
            _entities[index] = entity;
            _entityIndex.Remove(oldEntity);
            _entityIndex[entity] = index;
            ref var entry = ref _entries[index];
            entry.DispatcherId = EntityDispatcherCache.GetId(typeof(T));
            return;
        }

        EnsureCapacity();
        index = _count++;
        _entities[index] = entity;
        _entityIndex[entity] = index;

        var newEntry = new EntityEntry
        {
            Id = docId,
            DispatcherId = EntityDispatcherCache.GetId(typeof(T)),
            IsNew = !snapshot.HasValue || snapshot.Value.IsDefault
        };

        if (snapshot.HasValue && !snapshot.Value.IsDefault)
        {
            newEntry.SetSnapshot(snapshot.Value);
            if (snapshot.Value.TryGetElementOffset("_etag", out _))
            {
                newEntry.ExpectedETag = snapshot.Value.GetGuid("_etag");
            }
        }

        _entries.Add(newEntry);
        _docIdIndex[docId] = index;
    }

    public void Track(object entity, Guid expectedEtag)
    {
        if (entity == null) throw new ArgumentNullException(nameof(entity));

        var index = FindIndex(entity);
        if (index != -1)
        {
            ref var entry = ref _entries[index];
            entry.ExpectedETag = expectedEtag;
            entry.IsNew = false;
            return;
        }

        var docId = EntityIdAccessor.GetDocId(entity);
        EnsureCapacity();
        index = _count++;
        _entities[index] = entity;
        _entityIndex[entity] = index;

        var newEntry = new EntityEntry
        {
            Id = docId,
            DispatcherId = EntityDispatcherCache.GetId(entity.GetType()),
            ExpectedETag = expectedEtag,
            IsNew = false
        };

        _entries.Add(newEntry);
        _docIdIndex[docId] = index;
    }

    public void Evict(object? entity)
    {
        if (entity == null) return;

        var index = FindIndex(entity);
        if (index == -1) return;

        _entityIndex.Remove(entity);
        _docIdIndex.Remove(_entries[index].Id);

        var lastIndex = _count - 1;
        if (index != lastIndex)
        {
            // Move last element into the evicted slot and update its dict entries
            var movedEntity = _entities[lastIndex];
            var movedEntry = _entries[lastIndex];
            _entities[index] = movedEntity;
            _entries[index] = movedEntry;
            _entityIndex[movedEntity] = index;
            _docIdIndex[movedEntry.Id] = index;
        }

        _entities[lastIndex] = null!;
        _entries.RemoveAt(lastIndex);
        _count--;
    }

    public Guid? GetExpectedETag(object? entity)
    {
        if (entity == null) return null;

        var index = FindIndex(entity);
        return index != -1 ? _entries[index].ExpectedETag : null;
    }

    public void TrackDelete<T>(T entity)
    {
        if (entity == null) throw new ArgumentNullException(nameof(entity));

        var index = FindIndex(entity);
        if (index != -1)
        {
            ref var entry = ref _entries[index];
            entry.IsDeleted = true;
        }
        else
        {
            var docId = EntityIdAccessor.GetDocId(entity);
            // Check by ID too
            index = FindIndex(docId);
            if (index != -1)
            {
                var oldEntity = _entities[index];
                _entities[index] = entity;
                _entityIndex.Remove(oldEntity);
                _entityIndex[entity] = index;
                ref var entry = ref _entries[index];
                entry.IsDeleted = true;
                return;
            }

            EnsureCapacity();
            index = _count++;
            _entities[index] = entity;
            _entityIndex[entity] = index;

            var newEntry = new EntityEntry
            {
                Id = docId,
                DispatcherId = EntityDispatcherCache.GetId(typeof(T)),
                IsDeleted = true
            };

            _entries.Add(newEntry);
            _docIdIndex[docId] = index;
        }
    }

    public void RefreshSnapshots(ArenaAllocator newArena)
    {
        var newEntries = new ArenaList<EntityEntry>(newArena, Math.Max(64, _count));

        var newCount = 0;
        for (int i = 0; i < _count; i++)
        {
            var entry = _entries[i];
            var entity = _entities[i];

            if (entry.IsDeleted)
            {
                _entities[i] = null!; 
                continue;
            }

            var writer = new ArenaBsonWriter(newArena);
            var dispatcher = EntityDispatcherCache.GetDispatcher(entry.DispatcherId);
            dispatcher.UpdateSnapshot(entity, entry, ref writer, newArena);
            
            var snapshot = writer.Commit(newArena);
            entry.SetSnapshot(snapshot);
            entry.IsNew = false;

            if (snapshot.TryGetElementOffset("_etag", out _))
            {
                entry.ExpectedETag = snapshot.GetGuid("_etag");
            }

            var index = newCount++;
            _entities[index] = entity;
            newEntries.Add(entry);
        }

        for (int i = newCount; i < _count; i++)
        {
            _entities[i] = null!;
        }

        _count = newCount;
        _entries = newEntries;
        _arena = newArena;

        // Rebuild index dicts from compacted arrays
        _entityIndex.Clear();
        _docIdIndex.Clear();
        for (int i = 0; i < newCount; i++)
        {
            _entityIndex[_entities[i]] = i;
            _docIdIndex[newEntries[i].Id] = i;
        }
    }

    public void CollectDeletedIds(List<object> ids)
    {
        for (int i = 0; i < _count; i++)
        {
            ref var entry = ref _entries[i];
            if (entry.IsDeleted && !entry.IsNew)
            {
                var id = EntityIdAccessor.GetId(_entities[i]);
                if (id != null) ids.Add(id);
            }
        }
    }

    public BlittableBsonDocument? GetSnapshot(object entity)
    {
        var index = FindIndex(entity);
        if (index == -1) return null;
        
        var entry = _entries[index];
        if (entry.BsonPtr == null) return null;
        return entry.GetSnapshot(_arena);
    }

    public unsafe int GetPendingUpdates(PendingOperation[] buffer, object[] entities, ArenaAllocator tempArena)
    {
        int count = 0;
        var pathBuffer = stackalloc char[256];

        for (int i = 0; i < _count; i++)
        {
            var entity = _entities[i];
            ref var entry = ref _entries[i]; // ref so we can update it

            if (entry.IsDeleted && entry.IsNew)
            {
                continue;
            }

            var dispatcher = EntityDispatcherCache.GetDispatcher(entry.DispatcherId);
            var collectionName = _conventions.GetCollectionName(entity.GetType());
            var collectionId = GetCollectionId(collectionName);

            if (entry.IsDeleted)
            {
                buffer[count] = new PendingOperation
                {
                    Type = OperationType.Delete,
                    CollectionId = collectionId,
                    Id = entry.Id,
                    ExpectedEtag = entry.ExpectedETag,
                };
                entities[count++] = entity;
            }
            else if (entry.IsNew)
            {
                var newEtag = Guid.NewGuid();
                if (dispatcher.HasConcurrencyCheck)
                {
                    dispatcher.SetETag(entity, newEtag);
                }

                var writer = new ArenaBsonWriter(_arena);
                dispatcher.UpdateSnapshot(entity, entry, ref writer, _arena);
                var snapshot = writer.Commit(_arena);
                entry.SetSnapshot(snapshot);

                if (snapshot.TryGetElementOffset("_etag", out _))
                {
                    entry.ExpectedETag = snapshot.GetGuid("_etag");
                }

                buffer[count] = new PendingOperation
                {
                    Type = OperationType.Insert,
                    CollectionId = collectionId,
                    PayloadPtr = snapshot.Pointer,
                    PayloadLength = snapshot.Length,
                };
                entities[count++] = entity;
            }
            else
            {
                var currentDocId = EntityIdAccessor.GetDocId(entity);
                if (currentDocId != entry.Id)
                {
                    throw new InvalidOperationException(
                        $"The Id of entity of type {entity.GetType()} was changed after Store(). " +
                        "Mutating an entity's Id after tracking begins is not supported.");
                }

                var builder = new ArenaUpdateDefinitionBuilder(_arena, pathBuffer);
                dispatcher.BuildUpdate(entity, entry, ref builder, _arena, default);

                if (builder.HasChanges)
                {
                    var nextEtag = Guid.NewGuid();
                    // Stamp _etag when the type declares concurrency checking, OR when the
                    // tracked document already carries an _etag the type doesn't expose as a
                    // property (a brownfield document under external/hidden concurrency control).
                    // A type/document with neither never had _etag and must not gain one here.
                    if (dispatcher.HasConcurrencyCheck || entry.ExpectedETag != Guid.Empty)
                    {
                        if (dispatcher.HasConcurrencyCheck)
                        {
                            dispatcher.SetETag(entity, nextEtag);
                        }
                        builder.Set("_etag", nextEtag);
                    }
                    var updateDoc = builder.Build();

                    buffer[count] = new PendingOperation
                    {
                        Type = OperationType.Update,
                        CollectionId = collectionId,
                        Id = entry.Id,
                        ExpectedEtag = entry.ExpectedETag,
                        PayloadPtr = updateDoc.Pointer,
                        PayloadLength = updateDoc.Length
                    };
                    entities[count++] = entity;
                }
            }
        }

        return count;
    }
}

public interface IEntityDispatcher
{
    bool HasConcurrencyCheck { get; }
    void SetETag(object entity, Guid etag);
    void UpdateSnapshot(object entity, EntityEntry entry, ref ArenaBsonWriter writer, ArenaAllocator arena);
    void BuildUpdate(object entity, EntityEntry entry, ref ArenaUpdateDefinitionBuilder builder, ArenaAllocator arena, ReadOnlySpan<char> pathPrefix);
}

public static class EntityDispatcherCache
{
    private static readonly ConcurrentDictionary<Type, int> _typeToId = new();
    private static readonly List<IEntityDispatcher> _dispatchers = new();
    private static readonly object _lock = new();

    public static int GetId(Type type)
    {
        if (_typeToId.TryGetValue(type, out var id)) return id;

        lock (_lock)
        {
            if (_typeToId.TryGetValue(type, out id)) return id;

            id = _dispatchers.Count;
            var dispatcherType = typeof(EntityDispatcher<>).MakeGenericType(type);
            var dispatcher = (IEntityDispatcher)Activator.CreateInstance(dispatcherType)!;
            _dispatchers.Add(dispatcher);
            _typeToId[type] = id;
            return id;
        }
    }

    public static IEntityDispatcher GetDispatcher(int id) => _dispatchers[id];
}

internal class EntityDispatcher<T> : IEntityDispatcher
{
    public bool HasConcurrencyCheck => EntityIdAccessorUtility<T>.HasETag;

    public void SetETag(object entity, Guid etag)
    {
        EntityIdAccessorUtility<T>.SetETag((T)entity, etag);
    }

    public void UpdateSnapshot(object entity, EntityEntry entry, ref ArenaBsonWriter writer, ArenaAllocator arena)
    {
        DynamicBlittableSerializer<T>.SerializeDelegate(ref writer, (T)entity);
    }

    public void BuildUpdate(object entity, EntityEntry entry, ref ArenaUpdateDefinitionBuilder builder, ArenaAllocator arena, ReadOnlySpan<char> pathPrefix)
    {
        var tEntity = (T)entity;
        var snapshot = entry.GetSnapshot(arena);
        DynamicBlittableSerializer<T>.BuildUpdateDelegate(tEntity, snapshot, ref builder, arena, pathPrefix);
    }
}
