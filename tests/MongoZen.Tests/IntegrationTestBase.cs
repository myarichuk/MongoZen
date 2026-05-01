using MongoDB.Bson;
using MongoDB.Driver;
using Testcontainers.MongoDb;
using Xunit;

namespace MongoZen.Tests;

public abstract class IntegrationTestBase : IAsyncLifetime
{
    private static readonly Lazy<Task<(MongoDbContainer Container, MongoClient Client)>> ContainerLazy = new(async () =>
    {
        var container = new MongoDbBuilder()
            .WithUsername("")
            .WithPassword("")
            .WithCommand("--replSet", "rs0", "--bind_ip_all")
            .Build();

        await container.StartAsync();

        await container.ExecAsync([
            "mongosh", "--eval",
            "rs.initiate({_id:'rs0',members:[{_id:0,host:'localhost:27017'}]})"
        ]);

        var connectionString = container.GetConnectionString();

        if (!connectionString.Contains("replicaSet="))
        {
            connectionString += (connectionString.Contains("?") ? "&" : "?") + "replicaSet=rs0&directConnection=true";      
        }
        else if (!connectionString.Contains("directConnection="))
        {
            connectionString += "&directConnection=true";
        }

        var client = new MongoClient(connectionString);

        // Wait for primary
        for (int i = 0; i < 30; i++)
        {
            try
            {
                var admin = client.GetDatabase("admin");
                var hello = await admin.RunCommandAsync<BsonDocument>(new BsonDocument("hello", 1));
                if (hello.TryGetValue("isWritablePrimary", out var isPrimary) && isPrimary.AsBoolean)
                {
                    break;
                }
            }
            catch
            {
            }
            await Task.Delay(1000);
        }

        return (container, client);
    });

    private string? _databaseName;
    protected IMongoDatabase Database = null!;
    private MongoClient? _mongoClient;

    protected MongoClient Client => _mongoClient ?? throw new InvalidOperationException("Client not initialized.");

    public async Task InitializeAsync()
    {
        var (_, client) = await ContainerLazy.Value;
        _mongoClient = client;

        _databaseName = $"test_{Guid.NewGuid():N}";
        Database = _mongoClient.GetDatabase(_databaseName);
    }

    public Task DisposeAsync()
    {
        // For performance, we don't drop databases per test, as it's expensive.
        return Task.CompletedTask;
    }
}
