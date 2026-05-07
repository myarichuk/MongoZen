using System.Buffers;
using System.Collections.Concurrent;
using SharpArena.Allocators;
using MongoDB.Driver;
using MongoDB.Bson;
using MongoZen.Bson;
using MongoZen.ChangeTracking;

namespace MongoZen;

/// <summary>
/// A high-performance unit-of-work session for MongoDB.
/// </summary>
public sealed class DocumentSession : IDisposable
{
    private readonly DocumentStore _store;
    private readonly IMongoDatabase _database;
    private ChangeTracker _changeTracker;
    private readonly ConcurrentDictionary<object, object> _identityMap = new();
    private readonly ArenaAllocator _arena;
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

        var slice = rawBson.Slice;
        var doc = ArenaBsonReader.Read(slice.AccessBackingBytes(0), _arena);
        
        var entity = DynamicBlittableSerializer<T>.DeserializeDelegate(doc, _arena);
        _identityMap[id] = entity!;
        _changeTracker.Track(entity, doc);

        return entity;
    }

    public void Store<T>(T entity)
    {
        if (entity == null)
        {
            throw new ArgumentNullException(nameof(entity));
        }

        var id = EntityIdAccessor.GetId(entity);
        if (id == null)
        {
            throw new InvalidOperationException("Entity must have an ID property.");
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

            await EnsureTransactionStartedAsync(cancellationToken);

            // Sort buffer by CollectionId then Type to group operations
            Array.Sort(buffer, 0, count, Comparer<PendingOperation>.Create((a, b) => 
                a.CollectionId != b.CollectionId ? a.CollectionId.CompareTo(b.CollectionId) : a.Type.CompareTo(b.Type)));

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

            // Successfully saved, refresh snapshots for next call to SaveChangesAsync
            var newArena = new ArenaAllocator((nuint)_initialArenaSize);
            _changeTracker.RefreshSnapshots(newArena);
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

    private class SessionAdvancedOperations(DocumentSession session) : ISessionAdvancedOperations
    {
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
        _disposed = true;
    }
}
