using System.Buffers;
using System.Collections.Concurrent;
using System.Linq.Expressions;
using SharpArena.Allocators;
using MongoDB.Driver;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoZen.Bson;
using MongoZen.ChangeTracking;

namespace MongoZen;

/// <summary>
/// A high-performance unit-of-work session for MongoDB.
/// </summary>
public sealed class DocumentSession : IDocumentSession
{
    private readonly DocumentStore _store;
    private readonly IMongoDatabase _database;
    private ChangeTracker _changeTracker;
    private readonly ConcurrentDictionary<object, object> _identityMap = new();
    private readonly ArenaAllocator _arena;
    private ArenaAllocator? _snapshotArena;
    private readonly int _initialArenaSize;
    private IClientSessionHandle? _clientSession;
    private bool _disposed;

    internal DocumentConventions Conventions => _store.Conventions;
    public IMongoDatabase Database => _database;
    public IClientSessionHandle? ClientSession => _clientSession;
    public IAttachmentsSessionOperations Attachments { get; }

    private ISessionAdvancedOperations? _advanced;
    public ISessionAdvancedOperations Advanced => _advanced ??= new SessionAdvancedOperations(this);

    internal DocumentSession(DocumentStore store, int initialArenaSize)
    {
        _store = store;
        _database = store.Database;
        _initialArenaSize = initialArenaSize;
        _arena = new ArenaAllocator((nuint)initialArenaSize);
        _changeTracker = new ChangeTracker(_store.Conventions, _arena);
        Attachments = new AttachmentsSessionOperations(this);
    }

    public async ValueTask<T?> LoadAsync<T>(object id, CancellationToken ct = default)
    {
        if (_identityMap.TryGetValue(id, out var existing))
        {
            return (T)existing;
        }

        var collectionName = _store.Conventions.GetCollectionName(typeof(T));
        var collection = _store.GetRawCollection(collectionName);
        var filter = Builders<RawBsonDocument>.Filter.Eq("_id", _store.Conventions.CreateBsonValue(id));

        var cursor = _clientSession != null
            ? await collection.FindAsync(_clientSession, filter, cancellationToken: ct)
            : await collection.FindAsync(filter, cancellationToken: ct);

        var rawBson = await cursor.FirstOrDefaultAsync(ct);
        if (rawBson == null)
        {
            return default;
        }

        return MaterializeTracked<T>(rawBson, id);
    }

    public void Store<T>(T entity)
    {
        if (entity == null)
        {
            throw new ArgumentNullException(nameof(entity));
        }

        var id = EntityIdAccessor.GetId(entity);
        if (id is null || (id is string s && string.IsNullOrEmpty(s)))
        {
            if (EntityIdAccessorUtility<T>.HasSettableStringId)
            {
                var tag = _store.Conventions.GetCollectionName(typeof(T));
                id = _store.HiLo.GenerateNextId(tag);
                EntityIdAccessorUtility<T>.SetId(entity, (string)id);
            }
            else
            {
                throw new InvalidOperationException("Entity must have an ID property.");
            }
        }

        if (_identityMap.TryAdd(id, entity))
        {
            _changeTracker.Track(entity);
        }
    }

    public void Delete<T>(T entity)
    {
        if (entity == null)
        {
            throw new ArgumentNullException(nameof(entity));
        }

        _changeTracker.TrackDelete(entity);
    }

    public async Task SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(DocumentSession));

        var trackedCount = _changeTracker.TrackedCount;
        if (trackedCount == 0) return;

        var buffer = ArrayPool<PendingOperation>.Shared.Rent(trackedCount);
        var entities = ArrayPool<object>.Shared.Rent(trackedCount);
        try
        {
            var count = _changeTracker.GetPendingUpdates(buffer, entities, _arena);
            if (count == 0) return;

            // Sort buffer by CollectionId then Type to group operations
            Array.Sort(buffer, 0, count, Comparer<PendingOperation>.Create((a, b) =>
                a.CollectionId != b.CollectionId ? a.CollectionId.CompareTo(b.CollectionId) : a.Type.CompareTo(b.Type)));

            // Count distinct (CollectionId, Type) groups
            int groupCount = ComputeGroupCount(buffer, count);

            // If multi-group and transactions required, ensure transaction is started
            if (groupCount > 1 && _store.Conventions.RequireTransactions)
            {
                await EnsureTransactionStartedAsync(cancellationToken);
                if (_clientSession is not { IsInTransaction: true })
                {
                    throw new TransactionRequirementException(
                        $"A transaction is required for this multi-group (group count: {groupCount}) SaveChangesAsync operation, " +
                        "but the server does not support transactions or they are not available.");
                }
            }
            else
            {
                await EnsureTransactionStartedAsync(cancellationToken);
            }

            int start = 0;
            while (start < count)
            {
                int end = start;
                int currentCollectionId = buffer[start].CollectionId;
                var currentType = buffer[start].Type;
                while (end < count && buffer[end].CollectionId == currentCollectionId && buffer[end].Type == currentType)
                {
                    end++;
                }

                var collectionName = _changeTracker.GetCollectionName(currentCollectionId);
                
                // Build raw BSON command
                var cmdWriter = new ArenaBsonWriter(_arena);
                cmdWriter.WriteStartDocument();
                
                switch (currentType)
                {
                    case OperationType.Insert:
                        cmdWriter.WriteString("insert", collectionName);
                        cmdWriter.WriteStartArray("documents");
                        for (int i = start; i < end; i++)
                        {
                            cmdWriter.WriteName(i - start, BlittableBsonConstants.BsonType.Document);
                            unsafe { cmdWriter.WriteRaw(new ReadOnlySpan<byte>(buffer[i].PayloadPtr, buffer[i].PayloadLength)); }
                        }
                        cmdWriter.WriteEndArray();
                        break;

                    case OperationType.Update:
                        cmdWriter.WriteString("update", collectionName);
                        cmdWriter.WriteStartArray("updates");
                        for (int i = start; i < end; i++)
                        {
                            cmdWriter.WriteStartDocument(i - start);
                            
                            // q: { _id: ..., _etag: ... }
                            cmdWriter.WriteStartDocument("q");
                            var bsonId = buffer[i].Id.ToBsonValue() ?? _store.Conventions.CreateBsonValue(EntityIdAccessor.GetId(entities[i]));
                            cmdWriter.WriteBsonValue("_id", bsonId);
                            if (buffer[i].ExpectedEtag != Guid.Empty)
                            {
                                cmdWriter.WriteGuid("_etag", buffer[i].ExpectedEtag);
                            }
                            cmdWriter.WriteEndDocument();

                            // u: { ... }
                            cmdWriter.WriteName("u", BlittableBsonConstants.BsonType.Document);
                            unsafe { cmdWriter.WriteRaw(new ReadOnlySpan<byte>(buffer[i].PayloadPtr, buffer[i].PayloadLength)); }
                            
                            cmdWriter.WriteEndDocument();
                        }
                        cmdWriter.WriteEndArray();
                        break;

                    case OperationType.Delete:
                        cmdWriter.WriteString("delete", collectionName);
                        cmdWriter.WriteStartArray("deletes");
                        for (int i = start; i < end; i++)
                        {
                            cmdWriter.WriteStartDocument(i - start);
                            
                            // q: { _id: ..., _etag: ... }
                            cmdWriter.WriteStartDocument("q");
                            var bsonId = buffer[i].Id.ToBsonValue() ?? _store.Conventions.CreateBsonValue(EntityIdAccessor.GetId(entities[i]));
                            cmdWriter.WriteBsonValue("_id", bsonId);
                            if (buffer[i].ExpectedEtag != Guid.Empty)
                            {
                                cmdWriter.WriteGuid("_etag", buffer[i].ExpectedEtag);
                            }
                            cmdWriter.WriteEndDocument();

                            cmdWriter.WriteInt32("limit", 1);
                            cmdWriter.WriteEndDocument();
                        }
                        cmdWriter.WriteEndArray();
                        break;
                }

                cmdWriter.WriteBoolean("ordered", true);
                cmdWriter.WriteEndDocument();

                var cmdDoc = cmdWriter.Commit(_arena);
                
                using (var cmdBuffer = PooledByteBuffer.Rent(cmdDoc.AsReadOnlySpan()))
                {
                    var rawCmd = new RawBsonDocument(cmdBuffer);
                    
                    BsonDocument result;
                    if (_clientSession != null)
                    {
                        result = await _database.RunCommandAsync<BsonDocument>(_clientSession, rawCmd, cancellationToken: cancellationToken);
                    }
                    else
                    {
                        result = await _database.RunCommandAsync<BsonDocument>(rawCmd, cancellationToken: cancellationToken);
                    }

                    // Check for concurrency issues
                    int expectedCount = end - start;
                    int actualCount = result.GetValue("n", 0).AsInt32;

                    if (actualCount < expectedCount && currentType != OperationType.Insert)
                    {
                        await IdentifyConcurrencyConflictAsync(buffer, entities, start, end, cancellationToken);
                    }

                    // Cascading delete for attachments
                    if (currentType == OperationType.Delete)
                    {
                        for (int i = start; i < end; i++)
                        {
                            await Attachments.DeleteAllAsync(EntityIdAccessor.GetId(entities[i])!, cancellationToken);
                        }
                    }
                }

                start = end;
            }

            if (_clientSession is { IsInTransaction: true })
            {
                await _clientSession.CommitTransactionAsync(cancellationToken);
            }

            // Collect deleted IDs before snapshot refresh removes them from the tracker
            var deletedIds = new List<object>();
            _changeTracker.CollectDeletedIds(deletedIds);

            // Refresh snapshots into a new arena; old snapshot arena can be freed after migration
            var newArena = new ArenaAllocator((nuint)_initialArenaSize);
            _changeTracker.RefreshSnapshots(newArena);
            _snapshotArena?.Dispose();
            _snapshotArena = newArena;

            // Purge deleted entities from the identity map so subsequent LoadAsync hits the DB
            foreach (var deletedId in deletedIds)
            {
                _identityMap.TryRemove(deletedId, out _);
            }
        }
        catch
        {
            if (_clientSession is { IsInTransaction: true })
            {
                await _clientSession.AbortTransactionAsync(cancellationToken);
            }
            throw;
        }
        finally
        {
            ArrayPool<PendingOperation>.Shared.Return(buffer);
            ArrayPool<object>.Shared.Return(entities);
        }
    }

    private static int ComputeGroupCount(PendingOperation[] buffer, int count)
    {
        if (count == 0) return 0;

        int groupCount = 1;
        int currentCollectionId = buffer[0].CollectionId;
        var currentType = buffer[0].Type;

        for (int i = 1; i < count; i++)
        {
            if (buffer[i].CollectionId != currentCollectionId || buffer[i].Type != currentType)
            {
                groupCount++;
                currentCollectionId = buffer[i].CollectionId;
                currentType = buffer[i].Type;
            }
        }

        return groupCount;
    }

    internal async Task EnsureTransactionStartedAsync(CancellationToken token = default)
    {
        if (_clientSession != null)
        {
            return;
        }

        if (_store.Features.SupportsTransactions == false)
        {
            return;
        }

        try
        {
            _clientSession = await _database.Client.StartSessionAsync(cancellationToken: token);
            _clientSession.StartTransaction();
            _store.Features.SupportsTransactions = true;
        }
        catch (NotSupportedException)
        {
            _clientSession?.Dispose();
            _clientSession = null;
            _store.Features.SupportsTransactions = false;
        }
        catch (MongoException ex) when (ex.Message.Contains("sessions") || ex.Message.Contains("transaction"))
        {
            _clientSession?.Dispose();
            _clientSession = null;
            _store.Features.SupportsTransactions = false;
        }
    }

    private async Task IdentifyConcurrencyConflictAsync(PendingOperation[] buffer, object[] entities, int start, int end, CancellationToken ct)
    {
        // Only check updates and deletes
        var checkIndices = new List<int>();
        for (int i = start; i < end; i++)
        {
            if (buffer[i].Type == OperationType.Update || buffer[i].Type == OperationType.Delete)
            {
                checkIndices.Add(i);
            }
        }
        
        if (checkIndices.Count == 0)
        {
            return;
        }

        var collectionName = _changeTracker.GetCollectionName(buffer[start].CollectionId);
        var collection = _database.GetCollection<BsonDocument>(collectionName);
        
        var bsonIds = checkIndices.Select(idx => buffer[idx].Id.ToBsonValue() ?? _store.Conventions.CreateBsonValue(EntityIdAccessor.GetId(entities[idx]))).ToList();
        var filter = Builders<BsonDocument>.Filter.In("_id", bsonIds);
        
        var docs = await collection.Find(filter).ToListAsync(ct);
        var docMap = docs.ToDictionary(d => d["_id"], d => d);

        foreach (var idx in checkIndices)
        {
            var op = buffer[idx];
            object entity = entities[idx];
            object id = EntityIdAccessor.GetId(entity)!;
            var bsonId = op.Id.ToBsonValue() ?? _store.Conventions.CreateBsonValue(id);
            Guid expectedEtag = op.ExpectedEtag;

            if (!docMap.TryGetValue(bsonId, out var doc))
            {
                // Document is missing from DB (already deleted?)
                throw new ConcurrencyException($"Document with ID {id} was deleted by another user.", entity);
            }

            if (!doc.Contains("_etag"))
            {
                if (expectedEtag != Guid.Empty)
                {
                    throw new ConcurrencyException($"Document with ID {id} in collection {collectionName} does not have an _etag in the database (Expected ETag: {expectedEtag}).", entity);
                }
                continue; // Both missing, no conflict
            }

            var actualEtag = doc["_etag"].AsGuid;
            if (actualEtag != expectedEtag)
            {
                throw new ConcurrencyException($"Document with ID {id} was modified by another user (Expected ETag: {expectedEtag}, Actual: {actualEtag}).", entity);
            }
        }

        // If we reach here, we couldn't identify a specific mismatching ETag.
        throw new ConcurrencyException("A concurrency conflict occurred, but it could not be identified specifically.");
    }

    public BlittableBsonDocument? GetSnapshot(object entity) => _changeTracker.GetSnapshot(entity);

    /// <summary>
    /// Materialize a RawBsonDocument into a tracked entity, preserving identity map coherence.
    /// </summary>
    private T MaterializeTracked<T>(RawBsonDocument rawBson, object? precomputedId = null)
    {
        var slice = rawBson.Slice;
        var doc = ArenaBsonReader.Read(slice.AccessBackingBytes(0), _arena);
        var entity = DynamicBlittableSerializer<T>.DeserializeDelegate(doc, _arena);
        var id = precomputedId ?? EntityIdAccessor.GetId(entity!);

        if (id != null && _identityMap.TryGetValue(id, out var existing))
            return (T)existing; // Return existing tracked instance to preserve in-flight edits

        if (id != null) _identityMap[id] = entity!;
        _changeTracker.Track(entity, doc);
        return entity!;
    }

    /// <summary>
    /// Query entities by filter. Results are added to the identity map and change-tracked;
    /// mutations are persisted on the next SaveChangesAsync.
    /// </summary>
    public async ValueTask<IReadOnlyList<T>> QueryAsync<T>(
        FilterDefinition<T> filter,
        SortDefinition<T>? sort = null,
        int? skip = null,
        int? limit = null,
        CancellationToken ct = default)
    {
        var collectionName = _store.Conventions.GetCollectionName(typeof(T));
        var rawCollection = _store.GetRawCollection(collectionName);

        var serializer = BsonSerializer.SerializerRegistry.GetSerializer<T>();
        var renderArgs = new RenderArgs<T>(serializer, BsonSerializer.SerializerRegistry);
        var rawFilter = (FilterDefinition<RawBsonDocument>)filter.Render(renderArgs).AsBsonDocument;

        var find = _clientSession != null
            ? rawCollection.Find(_clientSession, rawFilter)
            : rawCollection.Find(rawFilter);

        if (sort != null)
            find = find.Sort((SortDefinition<RawBsonDocument>)sort.Render(renderArgs).AsBsonDocument);
        if (skip.HasValue)
            find = find.Skip(skip.Value);
        if (limit.HasValue)
            find = find.Limit(limit.Value);

        var results = new List<T>();
        using var cursor = await find.ToCursorAsync(ct);
        while (await cursor.MoveNextAsync(ct))
        {
            foreach (var rawBson in cursor.Current)
                results.Add(MaterializeTracked<T>(rawBson));
        }
        return results;
    }

    /// <summary>
    /// Query entities by expression filter. Results are added to the identity map and change-tracked;
    /// mutations are persisted on the next SaveChangesAsync.
    /// </summary>
    public ValueTask<IReadOnlyList<T>> QueryAsync<T>(
        Expression<Func<T, bool>> filter,
        SortDefinition<T>? sort = null,
        int? skip = null,
        int? limit = null,
        CancellationToken ct = default)
        => QueryAsync(Builders<T>.Filter.Where(filter), sort, skip, limit, ct);

    /// <summary>
    /// Run an aggregation pipeline. Results are NOT tracked; TResult is a read-only projection.
    /// </summary>
    public async ValueTask<IReadOnlyList<TResult>> AggregateAsync<T, TResult>(
        PipelineDefinition<T, TResult> pipeline,
        AggregateOptions? options = null,
        CancellationToken ct = default)
    {
        var collectionName = _store.Conventions.GetCollectionName(typeof(T));
        var collection = _database.GetCollection<T>(collectionName);

        var cursor = _clientSession != null
            ? await collection.AggregateAsync(_clientSession, pipeline, options, ct)
            : await collection.AggregateAsync(pipeline, options, ct);

        return await cursor.ToListAsync(ct);
    }

    /// <summary>
    /// Counts documents matching the filter.
    /// </summary>
    public async ValueTask<long> CountAsync<T>(FilterDefinition<T> filter, CancellationToken ct = default)
    {
        var collectionName = _store.Conventions.GetCollectionName(typeof(T));
        var rawCollection = _store.GetRawCollection(collectionName);

        var serializer = BsonSerializer.SerializerRegistry.GetSerializer<T>();
        var renderArgs = new RenderArgs<T>(serializer, BsonSerializer.SerializerRegistry);
        var rawFilter = (FilterDefinition<RawBsonDocument>)filter.Render(renderArgs).AsBsonDocument;

        return _clientSession != null
            ? await rawCollection.CountDocumentsAsync(_clientSession, rawFilter, cancellationToken: ct)
            : await rawCollection.CountDocumentsAsync(rawFilter, cancellationToken: ct);
    }

    /// <summary>
    /// Counts documents matching the expression filter.
    /// </summary>
    public ValueTask<long> CountAsync<T>(Expression<Func<T, bool>> filter, CancellationToken ct = default)
        => CountAsync(Builders<T>.Filter.Where(filter), ct);

    /// <summary>
    /// Checks if any documents match the filter.
    /// </summary>
    public async ValueTask<bool> AnyAsync<T>(FilterDefinition<T> filter, CancellationToken ct = default)
    {
        var collectionName = _store.Conventions.GetCollectionName(typeof(T));
        var rawCollection = _store.GetRawCollection(collectionName);

        var serializer = BsonSerializer.SerializerRegistry.GetSerializer<T>();
        var renderArgs = new RenderArgs<T>(serializer, BsonSerializer.SerializerRegistry);
        var rawFilter = (FilterDefinition<RawBsonDocument>)filter.Render(renderArgs).AsBsonDocument;

        var count = _clientSession != null
            ? await rawCollection.CountDocumentsAsync(_clientSession, rawFilter, new CountOptions { Limit = 1 }, cancellationToken: ct)
            : await rawCollection.CountDocumentsAsync(rawFilter, new CountOptions { Limit = 1 }, cancellationToken: ct);

        return count > 0;
    }

    /// <summary>
    /// Checks if any documents match the expression filter.
    /// </summary>
    public ValueTask<bool> AnyAsync<T>(Expression<Func<T, bool>> filter, CancellationToken ct = default)
        => AnyAsync(Builders<T>.Filter.Where(filter), ct);

    /// <summary>
    /// Streams documents without tracking them. Results are not persisted on SaveChangesAsync.
    /// Early termination (break) does not leak the underlying cursor.
    /// </summary>
    public async IAsyncEnumerable<T> StreamAsync<T>(
        FilterDefinition<T> filter,
        SortDefinition<T>? sort = null,
        int? skip = null,
        int? limit = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var collectionName = _store.Conventions.GetCollectionName(typeof(T));
        var rawCollection = _store.GetRawCollection(collectionName);

        var serializer = BsonSerializer.SerializerRegistry.GetSerializer<T>();
        var renderArgs = new RenderArgs<T>(serializer, BsonSerializer.SerializerRegistry);
        var rawFilter = (FilterDefinition<RawBsonDocument>)filter.Render(renderArgs).AsBsonDocument;

        var find = _clientSession != null
            ? rawCollection.Find(_clientSession, rawFilter)
            : rawCollection.Find(rawFilter);

        if (sort != null)
            find = find.Sort((SortDefinition<RawBsonDocument>)sort.Render(renderArgs).AsBsonDocument);
        if (skip.HasValue)
            find = find.Skip(skip.Value);
        if (limit.HasValue)
            find = find.Limit(limit.Value);

        using var cursor = await find.ToCursorAsync(ct);
        using var scratchArena = new ArenaAllocator(64 * 1024);
        while (await cursor.MoveNextAsync(ct))
        {
            foreach (var rawBson in cursor.Current)
            {
                var slice = rawBson.Slice;
                var doc = ArenaBsonReader.Read(slice.AccessBackingBytes(0), scratchArena);
                var entity = DynamicBlittableSerializer<T>.DeserializeDelegate(doc, scratchArena);
                yield return entity;
            }
        }
    }

    private class SessionAdvancedOperations(DocumentSession session) : ISessionAdvancedOperations
    {
        public bool IsTransactional => session._clientSession is { IsInTransaction: true };

        public Guid? GetETagFor(object entity) => session._changeTracker.GetExpectedETag(entity);

        public void Store(object entity, Guid expectedEtag)
        {
            if (entity == null)
            {
                throw new ArgumentNullException(nameof(entity));
            }

            var id = EntityIdAccessor.GetId(entity);
            if (id == null)
            {
                throw new InvalidOperationException("Entity must have an ID.");
            }

            session._identityMap[id] = entity;
            session._changeTracker.Track(entity, expectedEtag);
        }

        public void Evict(object entity)
        {
            if (entity == null)
            {
                return;
            }

            var id = EntityIdAccessor.GetId(entity);
            if (id != null)
            {
                session._identityMap.TryRemove(id, out _);
            }

            session._changeTracker.Evict(entity);
        }

        public async ValueTask RefreshAsync<T>(T entity, CancellationToken ct = default)
        {
            if (entity == null)
            {
                throw new ArgumentNullException(nameof(entity));
            }

            var id = EntityIdAccessor.GetId(entity);
            if (id == null)
            {
                throw new InvalidOperationException("Entity must have an ID to be refreshed.");
            }

            var collectionName = session._store.Conventions.GetCollectionName(typeof(T));
            var collection = session._database.GetCollection<BsonDocument>(collectionName);
            var filter = Builders<BsonDocument>.Filter.Eq("_id", session._store.Conventions.CreateBsonValue(id));

            var cursor = session._clientSession != null
                ? await collection.FindAsync(session._clientSession, filter, cancellationToken: ct)
                : await collection.FindAsync(filter, cancellationToken: ct);

            var bsonDoc = await cursor.FirstOrDefaultAsync(ct);
            if (bsonDoc != null)
            {
                // We convert BsonDocument to arena bytes for compatibility with the rest of the engine
                var bytes = bsonDoc.ToBson();
                var doc = ArenaBsonReader.Read(bytes, session._arena);

                DynamicBlittableSerializer<T>.DeserializeIntoDelegate(doc, session._arena, entity);
                session._changeTracker.Track(entity, doc);
            }
        }

        public async ValueTask<long> UpdateManyAsync<T>(FilterDefinition<T> filter, UpdateDefinition<T> update, CancellationToken ct = default)
        {
            var collectionName = session._store.Conventions.GetCollectionName(typeof(T));
            var collection = session._database.GetCollection<T>(collectionName);

            var result = session._clientSession != null
                ? await collection.UpdateManyAsync(session._clientSession, filter, update, cancellationToken: ct)
                : await collection.UpdateManyAsync(filter, update, cancellationToken: ct);

            return result.ModifiedCount;
        }

        public async ValueTask<long> DeleteManyAsync<T>(FilterDefinition<T> filter, CancellationToken ct = default)
        {
            var collectionName = session._store.Conventions.GetCollectionName(typeof(T));
            var collection = session._database.GetCollection<T>(collectionName);

            var result = session._clientSession != null
                ? await collection.DeleteManyAsync(session._clientSession, filter, cancellationToken: ct)
                : await collection.DeleteManyAsync(filter, cancellationToken: ct);

            return result.DeletedCount;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _clientSession?.Dispose();
        _changeTracker.Dispose();
        _arena.Dispose();
        _snapshotArena?.Dispose();
        _disposed = true;
    }
}
