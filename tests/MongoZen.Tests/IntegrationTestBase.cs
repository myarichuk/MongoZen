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
    private static readonly Lazy<Task<IMongoRunner>> RunnerLazy = new(async () =>
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
        return await MongoRunner.RunAsync(options);
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
        var runner = await RunnerLazy.Value;
        _mongoClient = new MongoClient(runner.ConnectionString);
        _databaseName = $"test_{Guid.NewGuid():N}";
        Database = _mongoClient.GetDatabase(_databaseName);

        // Ensure replica set is ready if needed (though usually RunAsync handles it)
        try
        {
            await _mongoClient.GetDatabase("admin")
                .RunCommandAsync<BsonDocument>(new BsonDocument("replSetGetStatus", 1));
        }
        catch
        {
            // Ignore if not a replica set or not ready
        }
    }

    /// <inheritdoc/>
    public async Task DisposeAsync()
    {
        if (_databaseName != null && _mongoClient != null)
        {
            try
            {
                await _mongoClient.DropDatabaseAsync(_databaseName);
            }
            catch
            {
            }
        }
        _mongoClient?.Dispose();
    }
}
