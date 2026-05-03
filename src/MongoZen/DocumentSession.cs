using System.Collections.Concurrent;
using SharpArena.Allocators;
using MongoDB.Driver;
using MongoDB.Bson;
using MongoZen.Bson;

namespace MongoZen;

/// <summary>
/// A high-performance, unit-of-work session for MongoDB.
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

    public IAttachmentsSessionOperations Attachments => _attachments ??= new AttachmentsSessionOperations(_database, _clientSession);

    /// <summary>
    /// Starts a new MongoDB transaction within this session.
    /// </summary>
    public async Task StartTransactionAsync(CancellationToken cancellationToken = default)
    {
        if (_store.Features.SupportsTransactions == false)
        {
            throw new NotSupportedException("The connected MongoDB cluster does not support transactions (must be a replica set or sharded cluster).");
        }

        _clientSession ??= await _database.Client.StartSessionAsync(cancellationToken: cancellationToken);

        if (_store.Features.SupportsTransactions == null)
        {
            // First time check
            var admin = _database.Client.GetDatabase("admin");
            var hello = await admin.RunCommandAsync<BsonDocument>(new BsonDocument("hello", 1), cancellationToken: cancellationToken);
            
            bool isReplicaSet = hello.Contains("setName") || (hello.Contains("isWritablePrimary") && hello["isWritablePrimary"].AsBoolean);
            _store.Features.SupportsTransactions = isReplicaSet;

            if (!isReplicaSet)
            {
                throw new NotSupportedException("The connected MongoDB cluster does not support transactions.");
            }
        }

        _clientSession.StartTransaction();
        
        if (_attachments != null)
        {
             _attachments = new AttachmentsSessionOperations(_database, _clientSession);
        }
    }

    /// <summary>
    /// Commits the current MongoDB transaction.
    /// </summary>
    public async Task CommitTransactionAsync(CancellationToken cancellationToken = default)
    {
        if (_clientSession == null) throw new InvalidOperationException("No transaction in progress.");
        await _clientSession.CommitTransactionAsync(cancellationToken);
    }

    /// <summary>
    /// Aborts the current MongoDB transaction.
    /// </summary>
    public async Task AbortTransactionAsync(CancellationToken cancellationToken = default)
    {
        if (_clientSession == null) throw new InvalidOperationException("No transaction in progress.");
        await _clientSession.AbortTransactionAsync(cancellationToken);
    }

    /// <summary>
    /// Loads a document from the database and tracks it in the session.
    /// </summary>
    public async Task<T?> LoadAsync<T>(object id, CancellationToken cancellationToken = default)
    {
        var docId = DocId.From(id);
        if (_identityMap.TryGetValue((typeof(T), docId), out var existing))
        {
            return (T)existing;
        }

        var collectionName = DocumentTypeTracker.GetDefaultCollectionName(typeof(T));
        var collection = _database.GetCollection<BsonDocument>(collectionName);
        
        var filter = Builders<BsonDocument>.Filter.Eq("_id", id);
        
        IAsyncCursor<BsonDocument> cursor;
        if (_clientSession != null)
            cursor = await collection.FindAsync(_clientSession, filter, cancellationToken: cancellationToken);
        else
            cursor = await collection.FindAsync(filter, cancellationToken: cancellationToken);

        var bson = await cursor.FirstOrDefaultAsync(cancellationToken);
        
        if (bson != null)
        {
            var rawBytes = bson.ToBson(); 
            var doc = ArenaBsonReader.Read(rawBytes, _arena);
            
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
        var groupedUpdates = _changeTracker.GetGroupedUpdates();
        bool hasChanges = false;

        foreach (var group in groupedUpdates)
        {
            var collectionName = group.Key;
            var updates = group.Value;
            var collection = _database.GetCollection<BsonDocument>(collectionName);

            var models = updates.Select(u => u.ToWriteModel()).ToList();
            if (models.Count == 0) continue;

            hasChanges = true;

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

        if (hasChanges)
        {
            // Double Buffering: Refresh snapshots into a new arena to reclaim memory
            var nextArena = new ArenaAllocator((nuint)_initialArenaSize);
            _changeTracker.RefreshSnapshots(nextArena);
            
            var oldArena = _arena;
            _arena = nextArena;
            oldArena.Dispose();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _clientSession?.Dispose();
        _arena.Dispose();
        _disposed = true;
    }
}
