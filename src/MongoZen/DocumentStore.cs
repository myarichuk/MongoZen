using System.Collections.Concurrent;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Conventions;
using MongoDB.Bson.Serialization.Serializers;
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
    /// Gets the conventions used by this DocumentStore instance.
    /// </summary>
    public DocumentConventions Conventions { get; }

    /// <summary>
    /// Initializes a new instance of the DocumentStore with an existing IMongoClient.
    /// </summary>
    public DocumentStore(IMongoClient client, string databaseName, DocumentConventions? conventions = null)
    {
        _client = client;
        _databaseName = databaseName;
        _database = _client.GetDatabase(databaseName);
        Conventions = conventions ?? new DocumentConventions();
        
        // For shared clients, we use the servers list as the key
        var servers = string.Join(",", client.Settings.Servers);
        _features = GetOrDiscoverFeatures(servers);

        // NOTE: The MongoDB Driver's ConventionRegistry is global. 
        // We register it here for convenience, but be aware that if multiple DocumentStore 
        // instances are created with different GuidRepresentations, the last one registered 
        // will apply to the entire AppDomain.
        var guidConvention = new ConventionPack { 
            new GuidSerializerConvention(Conventions.GuidRepresentation) 
        };
        ConventionRegistry.Register("GuidStandard", guidConvention, _ => true);
    }

    private ClusterFeatures GetOrDiscoverFeatures(string key) => new();

    /// <summary>
    /// Gets the underlying MongoDB database.
    /// </summary>
    public IMongoDatabase Database => _database;

    private readonly ConcurrentDictionary<string, IMongoCollection<RawBsonDocument>> _rawCollectionCache = new();

    public IMongoCollection<RawBsonDocument> GetRawCollection(string name)
    {
        return _rawCollectionCache.GetOrAdd(name, n => Database.GetCollection<RawBsonDocument>(n));
    }

    /// <summary>
    /// Gets the discovered features of the cluster.
    /// </summary>
    public ClusterFeatures Features => _features;

    /// <summary>
    /// Opens a new high-performance, unit-of-work session.
    /// </summary>
    /// <param name="initialArenaSize">The initial size of the arena allocator in bytes. Defaults to 1MB.</param>
    public DocumentSession OpenSession(int initialArenaSize = 1024 * 1024) => 
        new(this, initialArenaSize);

    /// <summary>
    /// Scans the specified assembly for all index creation tasks and executes them.
    /// </summary>
    public ValueTask ExecuteIndexesAsync(System.Reflection.Assembly assembly, CancellationToken cancellationToken = default)
        => IndexCreation.CreateIndexesAsync(assembly, this, cancellationToken);

    /// <summary>
    /// Scans the assembly containing the DocumentStore instance for all index creation tasks and executes them.
    /// </summary>
    public ValueTask ExecuteIndexesAsync(CancellationToken cancellationToken = default)
        => IndexCreation.CreateIndexesAsync(System.Reflection.Assembly.GetCallingAssembly(), this, cancellationToken);

    public void Dispose()
    {
        // MongoClient handles its own connection pooling
    }
}

public sealed class ClusterFeatures
{
    public bool? SupportsTransactions { get; internal set; }
}
