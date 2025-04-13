using EphemeralMongo;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Library.Tests;

public class IntegrationTestBase: IAsyncLifetime
{
    private readonly MongoRunnerOptions _options;
    private IMongoRunner _runner;
    private string _databaseName;
    protected IMongoDatabase? Database;
    private MongoClient _mongoClient;

    protected MongoClient Client => _mongoClient;

    public IntegrationTestBase(bool useSingleReplicaSet = true)
    {
        _options = new MongoRunnerOptions
        {
            Version = MongoVersion.V8,
            Edition = MongoEdition.Community,
            UseSingleNodeReplicaSet = useSingleReplicaSet,
            AdditionalArguments = ["--quiet"],
            StandardErrorLogger = Console.WriteLine,
            StandardOutputLogger = Console.WriteLine,
            ConnectionTimeout = TimeSpan.FromSeconds(10),
            DataDirectoryLifetime = TimeSpan.FromMinutes(30),
        };
    }

    public async Task InitializeAsync()
    {
        _runner = await MongoRunner.RunAsync(_options);
        _mongoClient = new MongoClient(_runner.ConnectionString);
        _databaseName = $"test_{Guid.NewGuid()}";
        Database = _mongoClient.GetDatabase(_databaseName);

        if (_options.UseSingleNodeReplicaSet)
        {
            await _mongoClient.GetDatabase("admin")
                .RunCommandAsync<BsonDocument>(new BsonDocument("replSetGetStatus", 1));
        }
    }

    public async Task DisposeAsync()
    {
        if (_databaseName != null)
        {
            await _mongoClient.DropDatabaseAsync(_databaseName);
        }

        _mongoClient.Dispose();
        _runner.Dispose();
    }
}