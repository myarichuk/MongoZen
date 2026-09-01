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
    private readonly bool _ownsClient;
    private readonly string _databaseName;
    private readonly IMongoDatabase _database;
    private readonly ClusterFeatures _features;
    private readonly HiLoIdGenerator _hiLo;
    private bool _disposed;

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
        : this(new MongoClient(connectionString), databaseName, conventions, ownsClient: true) { }

    public DocumentStore(IMongoClient client, string databaseName, DocumentConventions? conventions = null)
        : this(client, databaseName, conventions, ownsClient: false) { }

    private DocumentStore(IMongoClient client, string databaseName, DocumentConventions? conventions, bool ownsClient)
    {
        _client = client;
        _ownsClient = ownsClient;
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
        if (_disposed) return;
        _disposed = true;

        // Only dispose a client we created ourselves (the connection-string constructor); a
        // client passed in by the caller may be shared with other code and outlive this store.
        if (_ownsClient && _client is IDisposable disposableClient)
        {
            disposableClient.Dispose();
        }
    }
}

public sealed class ClusterFeatures
{
    // 0 = unknown, 1 = supported, 2 = not supported
    private volatile int _state = 0;
    private readonly SemaphoreSlim _discoveryGate = new(1, 1);

    public bool? SupportsTransactions
    {
        get => _state switch { 1 => true, 2 => false, _ => null };
        internal set => _state = value switch { true => 1, false => 2, _ => 0 };
    }

    /// <summary>
    /// Determines transaction support from cluster topology (replica set or sharded cluster)
    /// via a single "hello" command, rather than inferring it from write failures. Idempotent
    /// and safe to call concurrently; only the first caller does the round-trip.
    /// </summary>
    internal async ValueTask EnsureDiscoveredAsync(IMongoClient client, CancellationToken ct)
    {
        if (_state != 0) return;

        await _discoveryGate.WaitAsync(ct);
        try
        {
            if (_state != 0) return;

            var admin = client.GetDatabase("admin");
            BsonDocument result;
            try
            {
                result = await admin.RunCommandAsync<BsonDocument>(new BsonDocument("hello", 1), cancellationToken: ct);
            }
            catch (MongoCommandException)
            {
                // Servers predating "hello" (renamed from "isMaster" in MongoDB 5.0) use the legacy name.
                result = await admin.RunCommandAsync<BsonDocument>(new BsonDocument("isMaster", 1), cancellationToken: ct);
            }

            bool isReplicaSet = result.Contains("setName");
            bool isMongos = result.TryGetValue("msg", out var msg) && msg.IsString && msg.AsString == "isdbgrid";
            SupportsTransactions = isReplicaSet || isMongos;
        }
        finally
        {
            _discoveryGate.Release();
        }
    }
}
