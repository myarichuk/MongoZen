using MongoDB.Driver;
using Testcontainers.MongoDb;
using Xunit;

namespace MongoZen.Tests;

/// <summary>
/// Base class for tests that need a real MongoDB replica set — transaction commit/abort,
/// concurrency conflicts, and index conflict/recreate all depend on server behavior that
/// <see cref="IntegrationTestBase"/>'s Mongo.Fakes simulator does not reproduce (it isn't a
/// replica set, and several commands it doesn't implement, e.g. findAndModify, aggregate
/// pipeline stages, throw instead of behaving like the real server). Gated behind Docker
/// availability: if Docker can't be reached, tests derived from this class are skipped
/// rather than failed, so this suite still runs on machines without Docker installed.
/// </summary>
public abstract class TestcontainersIntegrationTestBase : IAsyncLifetime
{
    /// <summary>
    /// Set this to a connection string of an existing replica set to run these tests without Docker
    /// (e.g. a locally started <c>mongod --replSet</c>). When unset, a Testcontainers replica set is used.
    /// </summary>
    public const string ConnectionStringEnvVar = "MONGOZEN_TEST_CONNECTION_STRING";

    private static readonly Lazy<Task<(MongoDbContainer? Container, string? SkipReason)>> ContainerLazy = new(async () =>
    {
        try
        {
            var container = new MongoDbBuilder()
                .WithImage("mongo:7.0")
                .WithReplicaSet()
                .Build();
            await container.StartAsync();

            // xunit doesn't give test-assembly-level teardown here, and the container is
            // deliberately shared (Lazy) across every test in the run rather than started per
            // test — so tear it down when the process exits instead of leaking it on the host.
            AppDomain.CurrentDomain.ProcessExit += (_, _) => container.DisposeAsync().AsTask().GetAwaiter().GetResult();

            return (container, null);
        }
        catch (Exception ex)
        {
            return (null, $"Docker is not available for Testcontainers: {ex.Message}");
        }
    });

    private MongoClient? _mongoClient;
    private string? _databaseName;

    protected MongoClient Client => _mongoClient ?? throw new InvalidOperationException("Client not initialized.");
    protected IMongoDatabase Database { get; private set; } = null!;

    /// <summary>
    /// Non-null when the container failed to start (Docker unreachable, image pull failed, etc.).
    /// Tests should call <c>Skip.IfNot(SkipReason is null, SkipReason)</c> as their first line.
    /// </summary>
    protected string? SkipReason { get; private set; }

    public async Task InitializeAsync()
    {
        var external = Environment.GetEnvironmentVariable(ConnectionStringEnvVar);
        string connectionString;
        if (!string.IsNullOrWhiteSpace(external))
        {
            connectionString = external;
        }
        else
        {
            var (container, skipReason) = await ContainerLazy.Value;
            SkipReason = skipReason;
            if (container == null)
            {
                return;
            }

            connectionString = container.GetConnectionString();
        }

        _mongoClient = new MongoClient(connectionString);
        _databaseName = $"test_{Guid.NewGuid():N}";
        Database = _mongoClient.GetDatabase(_databaseName);
    }

    public Task DisposeAsync() => Task.CompletedTask;
}
