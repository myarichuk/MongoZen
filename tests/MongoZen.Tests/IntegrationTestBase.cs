using EphemeralMongo;
using MongoDB.Bson;
using MongoDB.Driver;
using System.Reflection;

namespace MongoZen.Tests;

/// <summary>
/// Provides a shared ephemeral MongoDB test harness for integration tests.
/// </summary>
public class IntegrationTestBase : IAsyncLifetime
{
    private readonly MongoRunnerOptions _options;
    private IMongoRunner _runner = null!;
    private string _databaseName = null!;
    protected IMongoDatabase? Database;
    private MongoClient _mongoClient = null!;

    /// <summary>
    /// Gets the MongoDB client connected to the ephemeral instance.
    /// </summary>
    protected MongoClient Client => _mongoClient;

    /// <summary>
    /// Initializes a new instance of the <see cref="IntegrationTestBase"/> class.
    /// </summary>
    /// <param name="useSingleReplicaSet">Whether the ephemeral instance should run as a single-node replica set.</param>
    public IntegrationTestBase(bool useSingleReplicaSet = true)
    {
        _options = new MongoRunnerOptions
        {
            Version = MongoVersion.V8,
            Edition = MongoEdition.Community,
            UseSingleNodeReplicaSet = useSingleReplicaSet,
            AdditionalArguments = [ "--quiet" ],
            StandardErrorLogger = Console.WriteLine,
            StandardOutputLogger = Console.WriteLine,
            ConnectionTimeout = TimeSpan.FromSeconds(10),
            DataDirectoryLifetime = TimeSpan.FromMinutes(30),
        };
    }

    /// <inheritdoc/>
    public async Task InitializeAsync()
    {
        try
        {
            _runner = await MongoRunner.RunAsync(_options);
        }
        catch (EphemeralMongoException ex)
        {
            var skipExceptionType = typeof(Assert).Assembly.GetType("Xunit.Sdk.SkipException");
            if (skipExceptionType is not null)
            {
                throw (Exception)Activator.CreateInstance(skipExceptionType, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public, binder: null, args: [$"Integration tests skipped because MongoDB could not be started: {ex.Message}"], culture: null)!;
            }

            throw;
        }

        _mongoClient = new MongoClient(_runner.ConnectionString);
        _databaseName = $"test_{Guid.NewGuid()}";
        Database = _mongoClient.GetDatabase(_databaseName);

        if (_options.UseSingleNodeReplicaSet)
        {
            await _mongoClient.GetDatabase("admin")
                .RunCommandAsync<BsonDocument>(new BsonDocument("replSetGetStatus", 1));
        }
    }

    /// <inheritdoc/>
    public async Task DisposeAsync()
    {
        if (_databaseName != null)
        {
            try
            {
                await _mongoClient.DropDatabaseAsync(_databaseName);
            }
            catch (ObjectDisposedException)
            {
            }
        }

        _mongoClient?.Dispose();
        _runner?.Dispose();
    }
}
