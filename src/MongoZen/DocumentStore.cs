using System.Collections.Concurrent;
using MongoDB.Driver;

namespace MongoZen;

/// <summary>
/// Thread-safe entry point for MongoZen. Manages the connection to MongoDB and creates sessions.
/// </summary>
public sealed class DocumentStore : IDisposable
{
    private readonly IMongoClient _client;
    private readonly string _databaseName;
    private readonly IMongoDatabase _database;
    private readonly ClusterFeatures _features;

    private static readonly ConcurrentDictionary<string, ClusterFeatures> TopologyCache = new();

    /// <summary>
    /// Initializes a new instance of the DocumentStore with a connection string and database name.
    /// </summary>
    public DocumentStore(string connectionString, string databaseName)
    {
        _client = new MongoClient(connectionString);
        _databaseName = databaseName;
        _database = _client.GetDatabase(databaseName);
        _features = GetOrDiscoverFeatures(connectionString);
    }

    /// <summary>
    /// Initializes a new instance of the DocumentStore with an existing IMongoClient.
    /// </summary>
    public DocumentStore(IMongoClient client, string databaseName)
    {
        _client = client;
        _databaseName = databaseName;
        _database = _client.GetDatabase(databaseName);
        
        // For shared clients, we use the servers list as the key
        var servers = string.Join(",", client.Settings.Servers);
        _features = GetOrDiscoverFeatures(servers);
    }

    private ClusterFeatures GetOrDiscoverFeatures(string key)
    {
        return TopologyCache.GetOrAdd(key, _ =>
        {
            // Eager discovery or lazy? 
            // For now, let's assume we'll discover on first transaction start
            // and update the object.
            return new ClusterFeatures();
        });
    }

    /// <summary>
    /// Gets the underlying MongoDB database.
    /// </summary>
    public IMongoDatabase Database => _database;

    /// <summary>
    /// Gets the discovered features of the cluster.
    /// </summary>
    public ClusterFeatures Features => _features;

    /// <summary>
    /// Opens a new high-performance, unit-of-work session.
    /// </summary>
    /// <param name="initialArenaSize">The initial size of the arena allocator in bytes. Defaults to 1MB.</param>
    public DocumentSession OpenSession(int initialArenaSize = 1024 * 1024)
    {
        return new DocumentSession(this, initialArenaSize);
    }

    public void Dispose()
    {
        // MongoClient handles its own connection pooling
    }
}

public sealed class ClusterFeatures
{
    public bool? SupportsTransactions { get; internal set; }
}
