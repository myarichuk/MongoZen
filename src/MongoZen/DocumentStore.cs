using System.Collections.Concurrent;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Conventions;
using MongoDB.Bson.Serialization.Serializers;
using MongoDB.Driver;
using MongoZen.HiLo;

namespace MongoZen;

/// <summary>
/// Thread-safe entry point for MongoZen. Manages the connection to MongoDB and creates sessions.
/// </summary>
public sealed class DocumentStore : IDocumentStore
{
    private readonly IMongoClient _client;
    private readonly string _databaseName;
    private readonly IMongoDatabase _database;
    private readonly ClusterFeatures _features;
    private readonly HiLoIdGenerator _hiLo;

    private static readonly ConcurrentDictionary<string, ClusterFeatures> TopologyCache = new();
    private static readonly object _guidRegistrationLock = new();
    private static GuidRepresentation? _registeredGuidRepresentation;

    /// <summary>
    /// Gets the conventions used by this DocumentStore instance.
    /// </summary>
    public DocumentConventions Conventions { get; }

    /// <summary>
    /// Initializes a new instance of the DocumentStore with an existing IMongoClient.
    /// </summary>
    public DocumentStore(string connectionString, string databaseName, DocumentConventions? conventions = null)
        : this(new MongoClient(connectionString), databaseName, conventions) { }

    public DocumentStore(IMongoClient client, string databaseName, DocumentConventions? conventions = null)
    {
        _client = client;
        _databaseName = databaseName;
        _database = _client.GetDatabase(databaseName);
        Conventions = conventions ?? new DocumentConventions();
        _hiLo = new HiLoIdGenerator(_database, Conventions);

        // For shared clients, we use the servers list as the key
        var servers = string.Join(",", client.Settings.Servers);
        _features = GetOrDiscoverFeatures(servers);

        // Guarded, idempotent registration of GUID convention
        lock (_guidRegistrationLock)
        {
            if (_registeredGuidRepresentation is null)
            {
                var guidConvention = new ConventionPack { new GuidSerializerConvention(Conventions.GuidRepresentation) };
                ConventionRegistry.Register("MongoZen.GuidStandard", guidConvention, _ => true);
                _registeredGuidRepresentation = Conventions.GuidRepresentation;
            }
            else if (_registeredGuidRepresentation != Conventions.GuidRepresentation)
            {
                throw new InvalidOperationException(
                    $"A DocumentStore with GuidRepresentation={_registeredGuidRepresentation} has already registered " +
                    "MongoDB driver conventions in this process. The driver's ConventionRegistry is process-global, so " +
                    $"mixing GuidRepresentation ({Conventions.GuidRepresentation} requested) across DocumentStore instances " +
                    "in one process is not supported.");
            }
        }
    }

    private ClusterFeatures GetOrDiscoverFeatures(string key) =>
        TopologyCache.GetOrAdd(key, _ => new ClusterFeatures());

    /// <summary>
    /// Gets the underlying MongoDB database.
    /// </summary>
    public IMongoDatabase Database => _database;

    private readonly ConcurrentDictionary<string, IMongoCollection<RawBsonDocument>> _rawCollectionCache = new();

    public IMongoCollection<RawBsonDocument> GetRawCollection(string name) => 
        _rawCollectionCache.GetOrAdd(name, n=> Database.GetCollection<RawBsonDocument>(n));

    /// <summary>
    /// Gets the discovered features of the cluster.
    /// </summary>
    public ClusterFeatures Features => _features;

    /// <summary>
    /// Gets the Hi/Lo ID generator for this store.
    /// </summary>
    internal HiLoIdGenerator HiLo => _hiLo;

    /// <summary>
    /// Opens a new high-performance, unit-of-work session.
    /// </summary>
    /// <param name="initialArenaSize">The initial size of the arena allocator in bytes. Defaults to 1MB.</param>
    public DocumentSession OpenSession(int initialArenaSize = 1024 * 1024) =>
        new(this, initialArenaSize);

    IDocumentSession IDocumentStore.OpenSession(int initialArenaSize) => OpenSession(initialArenaSize);

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
    // 0 = unknown, 1 = supported, 2 = not supported
    private volatile int _state = 0;

    public bool? SupportsTransactions
    {
        get => _state switch { 1 => true, 2 => false, _ => null };
        internal set => _state = value switch { true => 1, false => 2, _ => 0 };
    }
}
