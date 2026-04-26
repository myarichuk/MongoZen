using EphemeralMongo;
using MongoDB.Bson;
using MongoDB.Driver;
using System.Reflection;

namespace MongoZen.Tests;

/// <summary>
/// Provides a shared ephemeral MongoDB test harness for integration tests.
/// </summary>
public abstract class IntegrationTestBase : IAsyncLifetime
{
    /// <summary>
    /// Shared runner and client to avoid expensive startup and discovery costs per test.
    /// xUnit runs tests in parallel across classes, so they share this instance.
    /// </summary>
    private static readonly Lazy<Task<(IMongoRunner Runner, MongoClient Client)>> RunnerLazy = new(async () =>
    {
        var options = new MongoRunnerOptions
        {
            Version = MongoVersion.V8,
            Edition = MongoEdition.Community,
            UseSingleNodeReplicaSet = true,
            AdditionalArguments = [ "--quiet" ],
            ConnectionTimeout = TimeSpan.FromSeconds(10),
            DataDirectoryLifetime = TimeSpan.FromMinutes(30),
        };
        var runner = await MongoRunner.RunAsync(options);
        var client = new MongoClient(runner.ConnectionString);
        
        // One-time check for replica set readiness to avoid per-test ping overhead.
        try
        {
            await client.GetDatabase("admin")
                .RunCommandAsync<BsonDocument>(new BsonDocument("replSetGetStatus", 1));
        }
        catch { }
        
        return (runner, client);
    });

    private string? _databaseName;
    protected IMongoDatabase? Database;
    private MongoClient? _mongoClient;

    /// <summary>
    /// Gets the MongoDB client connected to the shared ephemeral instance.
    /// </summary>
    protected MongoClient Client => _mongoClient ?? throw new InvalidOperationException("Client not initialized.");

    protected IntegrationTestBase()
    {
    }

    /// <inheritdoc/>
    public async Task InitializeAsync()
    {
        var (runner, client) = await RunnerLazy.Value;
        _mongoClient = client;
        
        // Each test gets a unique database to ensure isolation during parallel execution.
        // We avoid dropping databases in DisposeAsync because it's an expensive metadata operation;
        // EphemeralMongo cleans up the entire data directory at the end of the run.
        _databaseName = $"test_{Guid.NewGuid():N}";
        Database = _mongoClient.GetDatabase(_databaseName);
    }

    /// <inheritdoc/>
    public Task DisposeAsync()
    {
        // No-op for performance. Database dropping is the primary bottleneck in integration tests.
        return Task.CompletedTask;
    }
}
