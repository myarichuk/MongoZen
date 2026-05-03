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

    internal async Task EnsureTransactionStartedAsync(CancellationToken cancellationToken = default)
    {
        if (_clientSession != null) return;

        if (_store.Features.SupportsTransactions == false) return;

        if (_store.Features.SupportsTransactions == null)
        {
            // First time check
            var admin = _database.Client.GetDatabase("admin");
            var hello = await admin.RunCommandAsync<BsonDocument>(new BsonDocument("hello", 1), cancellationToken: cancellationToken);
            
            bool isReplicaSet = hello.Contains("setName") || (hello.Contains("isWritablePrimary") && hello["isWritablePrimary"].AsBoolean);
            _store.Features.SupportsTransactions = isReplicaSet;

            if (!isReplicaSet) return;
        }

        _clientSession = await _database.Client.StartSessionAsync(cancellationToken: cancellationToken);
        _clientSession.StartTransaction();
    }

    /// <summary>
    /// Loads a document from the database and tracks it in the session.
    /// </summary>
    public Task<T?> LoadAsync<T>(ObjectId id, CancellationToken cancellationToken = default)
    {
        return LoadInternalAsync<T, ObjectId>(DocId.FromObjectId(id), id, cancellationToken);
    }

    /// <summary>
    /// Loads a document from the database and tracks it in the session.
    /// </summary>
    public Task<T?> LoadAsync<T>(Guid id, CancellationToken cancellationToken = default)
    {
        return LoadInternalAsync<T, Guid>(DocId.FromGuid(id), id, cancellationToken);
    }

    /// <summary>
    /// Loads a document from the database and tracks it in the session.
    /// </summary>
    public Task<T?> LoadAsync<T>(string id, CancellationToken cancellationToken = default)
    {
        return LoadInternalAsync<T, string>(DocId.FromString(id), id, cancellationToken);
    }

    /// <summary>
    /// Loads a document from the database and tracks it in the session.
    /// </summary>
    public Task<T?> LoadAsync<T>(object id, CancellationToken cancellationToken = default)
    {
        return LoadInternalAsync<T, object>(DocId.From(id), id, cancellationToken);
    }

    private async Task<T?> LoadInternalAsync<T, TId>(DocId docId, TId rawId, CancellationToken cancellationToken)
    {
        await EnsureTransactionStartedAsync(cancellationToken);
        
        if (_identityMap.TryGetValue((typeof(T), docId), out var existing))
        {
            return (T)existing;
        }

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
            
            _identityMap.TryAdd((typeof(T), docId), entity!);
            _changeTracker.Track(entity!, doc);
            
            return entity;
        }
        
        return default;
    }

    /// <summary>
    /// Tracks an entity for insertion.
    /// </summary>
    public void Store<T>(T entity)
    {
        if (entity == null) throw new ArgumentNullException(nameof(entity));
        
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
        if (entity == null) throw new ArgumentNullException(nameof(entity));
        
        var id = EntityIdAccessor.GetId(entity);
        if (id != null)
        {
            var docId = DocId.From(id);
            _identityMap.TryRemove((typeof(T), docId), out _);
        }

        _changeTracker.TrackDelete<T>(entity);
    }

    /// <summary>
    /// Saves all changes tracked in this session to the database using BulkWrite for efficiency.
    /// </summary>
    public async Task SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        await EnsureTransactionStartedAsync(cancellationToken);

        var groupedUpdates = _changeTracker.GetGroupedUpdates();
        bool hasChanges = groupedUpdates.Values.Any(v => v.Count > 0);

        if (!hasChanges) return;

        try
        {
            foreach (var group in groupedUpdates)
            {
                var collectionName = group.Key;
                var updates = group.Value;
                var collection = _database.GetCollection<BsonDocument>(collectionName);

                var models = updates.Select(u => u.ToWriteModel()).ToList();
                if (models.Count == 0) continue;

                if (_clientSession != null)
                    await collection.BulkWriteAsync(_clientSession, models, cancellationToken: cancellationToken);
                else
                    await collection.BulkWriteAsync(models, cancellationToken: cancellationToken);
                
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

    public void Dispose()
    {
        if (_disposed) return;
        _clientSession?.Dispose();
        _arena.Dispose();
        _disposed = true;
    }
}
