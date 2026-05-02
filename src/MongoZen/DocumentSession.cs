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

    public DocumentSession(IMongoDatabase database, int initialArenaSize = 1024 * 1024)
    {
        _database = database;
        _arena = new ArenaAllocator((nuint)initialArenaSize);
        _changeTracker = new ChangeTracker(_arena);
    }

    public IMongoDatabase Database => _database;
    public ArenaAllocator Arena => _arena;

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
        
        // 1. Load raw BsonDocument (the driver still allocates this, but it's the raw bytes we want)
        // Optimization: Use ReadRawBsonDocument if we can get it from the driver.
        var bson = await collection.Find(filter).FirstOrDefaultAsync(cancellationToken);
        
        if (bson != null)
        {
            var rawBytes = bson.ToBson(); // This is still a bit allocative, but it's the baseline.
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
        if (id == null)
        {
            // Auto-generate ID if possible?
            // For now, let's assume ID is set or it will fail later.
        }
        else
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
        // TODO: Implement soft or hard delete tracking
    }

    /// <summary>
    /// Saves all changes tracked in this session to the database.
    /// </summary>
    public async Task SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        var updates = _changeTracker.GetUpdates();
        foreach (var update in updates)
        {
            await update.ExecuteAsync(_database, cancellationToken);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _arena.Dispose();
        _disposed = true;
    }
}
