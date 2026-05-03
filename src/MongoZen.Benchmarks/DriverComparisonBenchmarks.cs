using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using MongoDB.Driver;
using MongoDB.Driver.GridFS;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using Testcontainers.MongoDb;

namespace MongoZen.Benchmarks;

[MemoryDiagnoser]
[CategoriesColumn]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
public class DriverComparisonBenchmarks
{
    private static readonly string DatabaseName = "BenchmarkDb";
    private static readonly string CollectionName = "LargePocos";

    private MongoDbContainer _container = null!;
    private IMongoClient _client = null!;
    private IMongoDatabase _db = null!;
    private IMongoCollection<LargePoco> _collection = null!;
    private IGridFSBucket _gridFs = null!;
    private DocumentStore _store = null!;
    private List<ObjectId> _ids = null!;
    private byte[] _attachmentData = null!;

    [GlobalSetup]
    public void Setup()
    {
        _container = new MongoDbBuilder("mongo:latest")
            .WithUsername("")
            .WithPassword("")
            .WithCommand("--replSet", "rs0", "--bind_ip_all")
            .Build();

        _container.StartAsync().GetAwaiter().GetResult();

        _container.ExecAsync([
            "mongosh", "--eval",
            "rs.initiate({_id:'rs0',members:[{_id:0,host:'localhost:27017'}]})"
        ]).GetAwaiter().GetResult();

        var connectionString = _container.GetConnectionString();

        if (!connectionString.Contains("replicaSet="))
        {
            connectionString += (connectionString.Contains("?") ? "&" : "?") + "replicaSet=rs0&directConnection=true";
        }
        else if (!connectionString.Contains("directConnection="))
        {
            connectionString += "&directConnection=true";
        }

        _client = new MongoClient(connectionString);

        // Wait for primary
        for (int i = 0; i < 30; i++)
        {
            try
            {
                var admin = _client.GetDatabase("admin");
                var hello = admin.RunCommand<BsonDocument>(new BsonDocument("hello", 1));
                if (hello.TryGetValue("isWritablePrimary", out var isPrimary) && isPrimary.AsBoolean)
                {
                    break;
                }
            }
            catch
            {
            }
            System.Threading.Thread.Sleep(1000);
        }

        _db = _client.GetDatabase(DatabaseName);
        _collection = _db.GetCollection<LargePoco>(CollectionName);
        _gridFs = new GridFSBucket(_db);
        _store = new DocumentStore(_client, DatabaseName);

        var items = new List<LargePoco>();
        for (int i = 0; i < 100; i++)
        {
            var poco = new LargePoco
            {
                Id = ObjectId.GenerateNewId(),
                Name = $"Name {i}",
                Timestamp = DateTime.UtcNow,
                Tags = ["tag1", "tag2"],
                Items = new List<ItemPoco>
                {
                    new ItemPoco { Name = "Item1", Value = 1.0, IsActive = true },
                    new ItemPoco { Name = "Item2", Value = 2.0, IsActive = false }
                }
            };
            items.Add(poco);
        }

        _collection.InsertMany(items);
        _ids = items.Select(x => x.Id).ToList();

        // 1MB of random data for attachments
        _attachmentData = new byte[1024 * 1024];
        new Random(42).NextBytes(_attachmentData);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _container.StopAsync().GetAwaiter().GetResult();
        _container.DisposeAsync().GetAwaiter().GetResult();
    }

    [Benchmark(Baseline = true, Description = "Driver Load (No Tx)")]
    [BenchmarkCategory("Load")]
    public async Task<List<LargePoco>> Driver_LoadAll()
    {
        var result = new List<LargePoco>();
        foreach (var id in _ids)
        {
            var doc = await _collection.Find(x => x.Id == id).FirstOrDefaultAsync();
            result.Add(doc);
        }
        return result;
    }

    [Benchmark(Description = "Driver Load (In Tx)")]
    [BenchmarkCategory("Load")]
    public async Task<List<LargePoco>> Driver_LoadAll_Tx()
    {
        using var session = await _client.StartSessionAsync();
        session.StartTransaction();
        var result = new List<LargePoco>();
        foreach (var id in _ids)
        {
            var doc = await _collection.Find(session, x => x.Id == id).FirstOrDefaultAsync();
            result.Add(doc);
        }
        await session.CommitTransactionAsync();
        return result;
    }

    [Benchmark(Description = "MongoZen Load (Auto Tx)")]
    [BenchmarkCategory("Load")]
    public async Task<List<LargePoco>> MongoZen_LoadAll()
    {
        using var session = _store.OpenSession();
        var result = new List<LargePoco>();
        foreach (var id in _ids)
        {
            var doc = await session.LoadAsync<LargePoco>(id);
            result.Add(doc!);
        }
        return result;
    }

    [Benchmark(Baseline = true, Description = "Driver Update (Individual, No Tx)")]
    [BenchmarkCategory("Update")]
    public async Task Driver_UpdateAll()
    {
        foreach (var id in _ids)
        {
            var doc = await _collection.Find(x => x.Id == id).FirstOrDefaultAsync();
            if (doc != null)
            {
                var filter = Builders<LargePoco>.Filter.Eq(x => x.Id, id);
                var update = Builders<LargePoco>.Update.Set(x => x.Name, "Updated Name");
                await _collection.UpdateOneAsync(filter, update);
            }
        }
    }

    [Benchmark(Description = "Driver Update (Individual, In Tx)")]
    [BenchmarkCategory("Update")]
    public async Task Driver_UpdateAll_Tx()
    {
        using var session = await _client.StartSessionAsync();
        session.StartTransaction();
        foreach (var id in _ids)
        {
            var doc = await _collection.Find(session, x => x.Id == id).FirstOrDefaultAsync();
            if (doc != null)
            {
                var filter = Builders<LargePoco>.Filter.Eq(x => x.Id, id);
                var update = Builders<LargePoco>.Update.Set(x => x.Name, "Updated Name");
                await _collection.UpdateOneAsync(session, filter, update);
            }
        }
        await session.CommitTransactionAsync();
    }

    [Benchmark(Description = "Driver Update (BulkWrite, In Tx)")]
    [BenchmarkCategory("Update")]
    public async Task Driver_BulkUpdate_Tx()
    {
        using var session = await _client.StartSessionAsync();
        session.StartTransaction();
        var updates = new List<WriteModel<LargePoco>>();
        foreach (var id in _ids)
        {
            var doc = await _collection.Find(session, x => x.Id == id).FirstOrDefaultAsync();
            if (doc != null)
            {
                var filter = Builders<LargePoco>.Filter.Eq(x => x.Id, id);
                var update = Builders<LargePoco>.Update.Set(x => x.Name, "Updated Name");
                updates.Add(new UpdateOneModel<LargePoco>(filter, update));
            }
        }
        if (updates.Count > 0)
        {
            await _collection.BulkWriteAsync(session, updates);
        }
        await session.CommitTransactionAsync();
    }

    [Benchmark(Description = "MongoZen Update (SaveChanges, Auto Tx)")]
    [BenchmarkCategory("Update")]
    public async Task MongoZen_UpdateAll()
    {
        using var session = _store.OpenSession();
        foreach (var id in _ids)
        {
            var doc = await session.LoadAsync<LargePoco>(id);
            doc!.Name = "Updated Name";
        }
        await session.SaveChangesAsync();
    }

    [Benchmark(Baseline = true, Description = "Driver GridFS Upload")]
    [BenchmarkCategory("Attachments")]
    public async Task Driver_GridFS_Upload()
    {
        using var stream = new MemoryStream(_attachmentData);
        await _gridFs.UploadFromStreamAsync("driver.bin", stream);
    }

    [Benchmark(Description = "MongoZen Attachment Upload (Auto Tx)")]
    [BenchmarkCategory("Attachments")]
    public async Task MongoZen_Attachment_Upload()
    {
        using var session = _store.OpenSession();
        using var stream = new MemoryStream(_attachmentData);
        await session.Attachments.StoreAsync("doc/1", "mongozen.bin", stream);
        await session.SaveChangesAsync();
    }

}

[Document]
public partial class LargePoco
{
    public ObjectId Id { get; set; }
    public string Name { get; set; } = "";
    public DateTime Timestamp { get; set; }
    public List<string> Tags { get; set; } = new();
    public List<ItemPoco> Items { get; set; } = new();
}

[Document]
public partial class ItemPoco
{
    public string Name { get; set; } = "";
    public double Value { get; set; }
    public bool IsActive { get; set; }
}
