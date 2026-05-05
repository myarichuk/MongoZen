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
    private readonly ChangeTracker _changeTracker;
    private readonly ConcurrentDictionary<(Type, DocId), object> _identityMap = new();
    private bool _disposed;
    private AttachmentsSessionOperations? _attachments;
    private IClientSessionHandle? _clientSession;
    private ArenaAllocator _arena;
    private readonly int _initialArenaSize;

    public DocumentSession(DocumentStore store, int initialArenaSize = 1024 * 1024)
    {
        _store = store;
        _database = store.Database;
        _initialArenaSize = initialArenaSize;
        _arena = new ArenaAllocator((nuint)initialArenaSize);
        _changeTracker = new ChangeTracker(_arena);
    }

    public IMongoDatabase Database => _database;
    public ArenaAllocator Arena => _arena;
    internal IClientSessionHandle? ClientSession => _clientSession;

    public IAttachmentsSessionOperations Attachments => _attachments ??= new AttachmentsSessionOperations(this);

    public ISessionAdvancedOperations Advanced => new AdvancedOperations(this);

    private class AdvancedOperations(DocumentSession session) : ISessionAdvancedOperations
    {
        public Guid? GetETagFor(object entity) => session._changeTracker.GetExpectedETag(entity);

        public void Store(object entity, Guid expectedEtag)
        {
            if (entity == null) throw new ArgumentNullException(nameof(entity));
            
            var id = EntityIdAccessor.GetId(entity);
            if (id != null)
            {
                var docId = DocId.From(id);
                session._identityMap.TryAdd((entity.GetType(), docId), entity);
            }

            session._changeTracker.Track(entity, expectedEtag);
        }

        public void Evict(object entity)
        {
            if (entity == null) return;

            var id = EntityIdAccessor.GetId(entity);
            if (id != null)
            {
                var docId = DocId.From(id);
                session._identityMap.TryRemove((entity.GetType(), docId), out _);
            }

            session._changeTracker.Evict(entity);
        }

        public async Task RefreshAsync<T>(T entity, CancellationToken ct = default)
        {
            if (entity == null) throw new ArgumentNullException(nameof(entity));

            var id = EntityIdAccessor.GetId(entity);
            if (id == null) throw new InvalidOperationException("Entity must have an ID to be refreshed.");

            var collectionName = DocumentTypeTracker.GetDefaultCollectionName(typeof(T));
            var collection = session._database.GetCollection<BsonDocument>(collectionName);
            var filter = Builders<BsonDocument>.Filter.Eq("_id", id);

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

        if (_store.Features.SupportsTransactions == null)
        {
            var admin = _database.Client.GetDatabase("admin");
            var hello = await admin.RunCommandAsync<BsonDocument>(new BsonDocument("hello", 1), cancellationToken: token);
            
            bool isReplicaSet = hello.Contains("setName") || (hello.Contains("isWritablePrimary") && hello["isWritablePrimary"].AsBoolean);
            bool isSharded = hello.Contains("msg") && hello["msg"].AsString == "isdbgrid";
            _store.Features.SupportsTransactions = isReplicaSet || isSharded;

            if (!_store.Features.SupportsTransactions.Value)
            {
                return;
            }
        }

        _clientSession = await _database.Client.StartSessionAsync(cancellationToken: token);
        _clientSession.StartTransaction();
    }

    /// <summary>
    /// Loads a document from the database and tracks it in the session.
    /// </summary>
    public Task<T?> LoadAsync<T>(ObjectId id, CancellationToken cancellationToken = default) => 
        LoadInternalAsync<T, ObjectId>(DocId.FromObjectId(id), id, cancellationToken);

    /// <summary>
    /// Loads a document from the database and tracks it in the session.
    /// </summary>
    public Task<T?> LoadAsync<T>(Guid id, CancellationToken cancellationToken = default) => 
        LoadInternalAsync<T, Guid>(DocId.FromGuid(id), id, cancellationToken);

    /// <summary>
    /// Loads a document from the database and tracks it in the session.
    /// </summary>
    public Task<T?> LoadAsync<T>(string id, CancellationToken cancellationToken = default) => 
        LoadInternalAsync<T, string>(DocId.FromString(id), id, cancellationToken);

    /// <summary>
    /// Loads a document from the database and tracks it in the session.
    /// </summary>
    public Task<T?> LoadAsync<T>(object id, CancellationToken cancellationToken = default) => 
        LoadInternalAsync<T, object>(DocId.From(id), id, cancellationToken);

    private async Task<T?> LoadInternalAsync<T, TId>(DocId docId, TId rawId, CancellationToken cancellationToken)
    {
        if (_identityMap.TryGetValue((typeof(T), docId), out var existing))
        {
            return (T)existing;
        }

        await EnsureTransactionStartedAsync(cancellationToken);
        
        var collectionName = DocumentTypeTracker.GetDefaultCollectionName(typeof(T));
        var collection = _store.GetRawCollection(collectionName);
        
        var filter = Builders<RawBsonDocument>.Filter.Eq("_id", rawId);

        var cursor = _clientSession != null
            ? await collection.FindAsync(_clientSession, filter, cancellationToken: cancellationToken)
            : await collection.FindAsync(filter, cancellationToken: cancellationToken);

        var rawBson = await cursor.FirstOrDefaultAsync(cancellationToken);
        
        if (rawBson != null)
        {
            // access raw bytes directly without re-serialization
            var slice = rawBson.Slice;
            var doc = ArenaBsonReader.Read(slice.AccessBackingBytes(0), _arena);
            
            var entity = DynamicBlittableSerializer<T>.DeserializeDelegate(doc, _arena);
            
            if (_identityMap.TryAdd((typeof(T), docId), entity!))
            {
                _changeTracker.Track(entity!, doc);
                return entity;
            }
            
            return (T)_identityMap[(typeof(T), docId)];
        }
        
        return default;
    }

    /// <summary>
    /// Tracks an entity for insertion.
    /// </summary>
    public void Store<T>(T entity)
    {
        if (entity == null)
        {
            throw new ArgumentNullException(nameof(entity));
        }

        var id = EntityIdAccessor.GetId(entity);
        if (id != null)
        {
            var docId = DocId.From(id);
            _identityMap.TryAdd((typeof(T), docId), entity);
        }

        _changeTracker.Track(entity);
    }

    /// <summary>
    /// Deletes an entity from the database.
    /// </summary>
    public void Delete<T>(T entity)
    {
        if (entity == null)
        {
            throw new ArgumentNullException(nameof(entity));
        }

        var id = EntityIdAccessor.GetId(entity);
        if (id != null)
        {
            var docId = DocId.From(id);
            _identityMap.TryRemove((typeof(T), docId), out _);
        }

        _changeTracker.TrackDelete<T>(entity);
    }

    /// <summary>
    /// Gets the BSON snapshot of a tracked entity.
    /// </summary>
    public BlittableBsonDocument? GetSnapshot(object entity) => _changeTracker.GetSnapshot(entity);

    /// <summary>
    /// Saves all changes tracked in this session to the database using BulkWrite for efficiency.
    /// </summary>
    public async Task SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        var groupedUpdates = _changeTracker.GetGroupedUpdates();
        bool hasChanges = groupedUpdates.Values.Any(v => v.Count > 0);

        if (!hasChanges)
        {
            return;
        }

        await EnsureTransactionStartedAsync(cancellationToken);

        try
        {
            foreach (var group in groupedUpdates)
            {
                var collectionName = group.Key;
                var updates = group.Value;
                var collection = _database.GetCollection<BsonDocument>(collectionName);

                var models = updates.Select(u => u.ToWriteModel()).ToList();
                if (models.Count == 0)
                {
                    continue;
                }

                BulkWriteResult<BsonDocument> result;
                try
                {
                    if (_clientSession != null)
                    {
                        result = await collection.BulkWriteAsync(_clientSession, models, cancellationToken: cancellationToken);
                    }
                    else
                    {
                        result = await collection.BulkWriteAsync(models, cancellationToken: cancellationToken);
                    }
                }
                catch (Exception ex) when (ex is MongoCommandException or MongoBulkWriteException)
                {
                    await IdentifyConcurrencyConflictAsync(updates, cancellationToken);
                    throw;
                }

                var expectedMatched = updates.Count(u => u is UpdateOperation);
                var expectedDeleted = updates.Count(u => u is DeleteOperation);

                if (result.MatchedCount < expectedMatched || result.DeletedCount < expectedDeleted)
                {
                    await IdentifyConcurrencyConflictAsync(updates, cancellationToken);
                }

                // Cascading delete for attachments
                foreach (var update in updates)
                {
                    if (update is DeleteOperation del)
                    {
                        await Attachments.DeleteAllAsync(del.Id, cancellationToken);
                    }
                }
            }

            if (_clientSession != null && _clientSession.IsInTransaction)
            {
                await _clientSession.CommitTransactionAsync(cancellationToken);
            }
        }
        catch
        {
            if (_clientSession != null && _clientSession.IsInTransaction)
            {
                await _clientSession.AbortTransactionAsync(cancellationToken);
            }
            throw;
        }
        finally
        {
            if (_clientSession != null)
            {
                _clientSession.Dispose();
                _clientSession = null;
            }
        }

        // Double Buffering: Refresh snapshots into a new arena to reclaim memory
        var nextArena = new ArenaAllocator((nuint)_initialArenaSize);
        _changeTracker.RefreshSnapshots(nextArena);
        
        var oldArena = _arena;
        _arena = nextArena;
        oldArena.Dispose();
    }

    private async Task IdentifyConcurrencyConflictAsync(List<IPendingUpdate> updates, CancellationToken ct)
    {
        foreach (var update in updates)
        {
            object id;
            Guid expectedEtag;
            object entity;
            string collectionName;

            if (update is UpdateOperation uo)
            {
                id = uo.Id;
                expectedEtag = uo.ExpectedEtag;
                entity = uo.Entity;
                collectionName = uo.CollectionName;
            }
            else if (update is DeleteOperation del)
            {
                id = del.Id;
                expectedEtag = del.ExpectedEtag;
                entity = del.Entity;
                collectionName = del.CollectionName;
            }
            else
            {
                continue;
            }

            var collection = _database.GetCollection<BsonDocument>(collectionName);
            var filter = Builders<BsonDocument>.Filter.Eq("_id", id);
            var projection = Builders<BsonDocument>.Projection.Include("_etag");

            var doc = await collection.Find(filter).Project(projection).FirstOrDefaultAsync(ct);
            if (doc == null)
            {
                throw new ConcurrencyException($"Document with ID {id} in collection {collectionName} was deleted by another user.", entity);
            }

            if (!doc.Contains("_etag"))
            {
                throw new ConcurrencyException($"Document with ID {id} in collection {collectionName} does not have an _etag in the database.", entity);
            }

            var actualEtag = doc["_etag"].AsGuid;
            if (actualEtag != expectedEtag)
            {
                throw new ConcurrencyException($"Document with ID {id} in collection {collectionName} was modified by another user (Expected ETag: {expectedEtag}, Actual: {actualEtag}).", entity);
            }
        }

        throw new ConcurrencyException("A concurrency conflict occurred, but it could not be identified specifically.");
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _clientSession?.Dispose();
        _arena.Dispose();
        _disposed = true;
    }
}
