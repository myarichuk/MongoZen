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
    private readonly IMongoDatabase _database;
    private readonly ArenaAllocator _arena;
    private readonly ChangeTracker _changeTracker;
    private readonly ConcurrentDictionary<(Type, object), object> _identityMap = new();
    private bool _disposed;
    private AttachmentsSessionOperations? _attachments;
    private IClientSessionHandle? _clientSession;

    public DocumentSession(IMongoDatabase database, int initialArenaSize = 1024 * 1024)
    {
        _database = database;
        _arena = new ArenaAllocator((nuint)initialArenaSize);
        _changeTracker = new ChangeTracker(_arena);
    }

    public IMongoDatabase Database => _database;
    public ArenaAllocator Arena => _arena;

    public IAttachmentsSessionOperations Attachments => _attachments ??= new AttachmentsSessionOperations(_database, _clientSession);

    public async Task StartTransactionAsync(CancellationToken cancellationToken = default)
    {
        _clientSession ??= await _database.Client.StartSessionAsync(cancellationToken: cancellationToken);
        _clientSession.StartTransaction();
        // If attachments were already created, they won't use this session. 
        // In a real implementation, we should probably pass a provider or ensure order.
        // For now, we'll recreate the operations if they exist.
        if (_attachments != null)
        {
             _attachments = new AttachmentsSessionOperations(_database, _clientSession);
        }
    }

    public async Task CommitTransactionAsync(CancellationToken cancellationToken = default)
    {
        if (_clientSession == null) throw new InvalidOperationException("No transaction in progress.");
        await _clientSession.CommitTransactionAsync(cancellationToken);
    }

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
        if (_identityMap.TryGetValue((typeof(T), id), out var existing))
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
            
            _identityMap.TryAdd((typeof(T), id), entity!);
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
            _identityMap.TryAdd((typeof(T), id), entity);
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
            _identityMap.TryRemove((typeof(T), id), out _);
        }

        _changeTracker.TrackDelete<T>(entity);
    }

    /// <summary>
    /// Saves all changes tracked in this session to the database.
    /// </summary>
    public async Task SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        var updates = _changeTracker.GetUpdates();
        foreach (var update in updates)
        {
            await update.ExecuteAsync(_database, _clientSession, cancellationToken);
            
            // Cascading delete for attachments
            if (update is DeleteOperation del)
            {
                await Attachments.DeleteAllAsync(del.Id, cancellationToken);
            }
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
