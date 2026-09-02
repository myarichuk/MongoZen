using MongoDB.Bson;
using MongoDB.Driver;
using Mongo.Fakes.Server;
using Xunit;

namespace MongoZen.Tests;

public abstract class IntegrationTestBase : IAsyncLifetime
{
    static IntegrationTestBase()
    {
        try
        {
            MongoDB.Bson.Serialization.BsonSerializer.RegisterSerializer(new MongoDB.Bson.Serialization.Serializers.GuidSerializer(GuidRepresentation.Standard));
        }
        catch (MongoDB.Bson.BsonSerializationException)
        {
            // Already registered
        }
    }

    private static readonly Lazy<Task<MongoClient>> ClientLazy = new(async () =>
    {
        var baseline = new EmptyBaselineProvider();
        var backend = new BsonFileBackend(baseline);
        var server = new MongoFakeServer(backend, port: 0);
        await server.StartAsync();

        var connectionString = $"mongodb://127.0.0.1:{server.Port}/?directConnection=true";
        var client = new MongoClient(connectionString);

        // Verify connection
        try
        {
            await client.GetDatabase("admin").RunCommandAsync<BsonDocument>(new BsonDocument("ping", 1));
        }
        catch
        {
            throw new InvalidOperationException("Failed to connect to mongo.fakes server.");
        }

        return client;
    });

    private string? _databaseName;
    protected IMongoDatabase Database = null!;
    private MongoClient? _mongoClient;

    protected MongoClient Client => _mongoClient ?? throw new InvalidOperationException("Client not initialized.");

    public async Task InitializeAsync()
    {
        var client = await ClientLazy.Value;
        _mongoClient = client;

        _databaseName = $"test_{Guid.NewGuid():N}";
        Database = _mongoClient.GetDatabase(_databaseName);
    }

    public Task DisposeAsync()
    {
        // For performance, we don't drop databases per test, as it's expensive.
        return Task.CompletedTask;
    }

    private class EmptyBaselineProvider : IBaselineDataProvider
    {
        public IReadOnlyList<BsonDocument> GetCollection(string database, string collection) => Array.Empty<BsonDocument>();
        public IReadOnlyList<string> GetDatabases() => Array.Empty<string>();
        public IReadOnlyList<string> GetCollections(string database) => Array.Empty<string>();
    }
}
