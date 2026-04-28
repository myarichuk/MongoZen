using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.GridFS;
using MongoZen;
using Testcontainers.MongoDb;

namespace MongoZen.Benchmarks;

[MemoryDiagnoser]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
[MinColumn, MaxColumn]
[WarmupCount(3)]
public class ComparisonBenchmarks
{
    private MongoDbContainer _container = null!;
    private MongoClient _client = null!;
    private IMongoDatabase _database = null!;
    private IMongoCollection<BenchmarkEntity> _collection = null!;
    private IGridFSBucket _bucket = null!;
    private BenchmarkDbContext _dbContext = null!;
    private List<BenchmarkEntity> _testData = null!;
    private List<string> _testIds = null!;
    private byte[] _attachmentData = null!;

    [Params(100, 1000)]
    public int EntityCount;

    [GlobalSetup]
    public async Task Setup()
    {
        _container = new MongoDbBuilder()
            .WithUsername("")
            .WithPassword("")
            .WithCommand("--replSet", "rs0", "--bind_ip_all")
            .Build();

        await _container.StartAsync();

        await _container.ExecAsync([
            "mongosh", "--eval",
            "rs.initiate({_id:'rs0',members:[{_id:0,host:'localhost:27017'}]})"
        ]);

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

        for (int i = 0; i < 30; i++)
        {
            try
            {
                var admin = _client.GetDatabase("admin");
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

        _database = _client.GetDatabase("MongoZen_Benchmarks");
        _collection = _database.GetCollection<BenchmarkEntity>("Entities");
        _bucket = new GridFSBucket(_database);

        var options = new DbContextOptions(_database);
        _dbContext = new BenchmarkDbContext(options);

        _testData = Enumerable.Range(0, EntityCount).Select(i => new BenchmarkEntity
        {
            Id = $"entity/{i}",
            Name = $"Name {i}",
            Age = i % 100,
            Bio = "This is a long bio string to make serialization more expensive. " + string.Join(" ", Enumerable.Repeat("bla", 10)),
            CreatedAt = DateTime.UtcNow,
            Tags = ["tag1", "tag2", "tag3"],
            Metadata = new Dictionary<string, string> { { "key1", "value1" }, { "key2", "value2" } },
            PolymorphicData = i % 2 == 0 
                ? new DerivedDataA { Type = "A", InfoA = "Some info A" }
                : new DerivedDataB { Type = "B", InfoB = i },
            Version = 1
        }).ToList();

        _testIds = _testData.Select(e => e.Id).ToList();

        // 1MB attachment for GridFS tests
        _attachmentData = new byte[1024 * 1024];
        new Random(42).NextBytes(_attachmentData);
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        await _container.DisposeAsync();
    }

    [IterationSetup(Targets = [
        nameof(Insert_RawDriver_Bulk),
        nameof(Insert_MongoZen_OptimisticConcurrency),
        nameof(Insert_MongoZen_NoConcurrency)])]
    public void IterationSetup_Empty()
    {
        _database.DropCollection("Entities");
    }

    [IterationSetup(Targets = [
        nameof(ReadAndModify_RawDriver_Replace_NoConcurrency),
        nameof(ReadAndModify_RawDriver_Replace_ManualConcurrency),
        nameof(ReadAndModify_RawDriver_Set_NoConcurrency),
        nameof(ReadAndModify_RawDriver_Set_ManualConcurrency),
        nameof(ReadAndModify_MongoZen_Set_OptimisticConcurrency),
        nameof(ReadAndModify_MongoZen_Set_NoConcurrency)])]
    public void IterationSetup_WithData()
    {
        _database.DropCollection("Entities");
        _collection.InsertMany(_testData);
    }

    [IterationSetup(Targets = [
        nameof(IdentityMap_RawDriver_NoTracking),
        nameof(IdentityMap_MongoZen_FromMemory)])]
    public void IterationSetup_SingleDocument()
    {
        _database.DropCollection("Entities");
        _collection.InsertOne(_testData[0]);
    }

    [IterationSetup(Targets = [
        nameof(Attachments_RawDriver_GridFSBucket),
        nameof(Attachments_MongoZen_Optimized)])]
    public void IterationSetup_GridFS()
    {
        _database.DropCollection("fs.files");
        _database.DropCollection("fs.chunks");
    }

    // -------------------------------------------------------------------------
    // Insert benchmarks
    // -------------------------------------------------------------------------

    [BenchmarkCategory("Insert"), Benchmark(Baseline = true, Description = "Raw: InsertManyAsync")]
    public async Task Insert_RawDriver_Bulk()
    {
        await _collection.InsertManyAsync(_testData);
    }

    [BenchmarkCategory("Insert"), Benchmark(Description = "Zen: Store() + SaveChangesAsync (Concurrency ON)")]
    public async Task Insert_MongoZen_OptimisticConcurrency()
    {
        await using var session = await BenchmarkDbContextSession.OpenSessionAsync(_dbContext);
        foreach (var entity in _testData)
        {
            session.Store(entity);
        }
        await session.SaveChangesAsync();
    }

    [BenchmarkCategory("Insert"), Benchmark(Description = "Zen: Store() + SaveChangesAsync (Concurrency OFF)")]
    public async Task Insert_MongoZen_NoConcurrency()
    {
        var options = new DbContextOptions(_database, new Conventions { ConcurrencyPropertyName = null });
        var db = new BenchmarkDbContext(options);
        await using var session = await BenchmarkDbContextSession.OpenSessionAsync(db);
        foreach (var entity in _testData)
        {
            session.Store(entity);
        }
        await session.SaveChangesAsync();
    }

    // -------------------------------------------------------------------------
    // Read + modify benchmarks
    // -------------------------------------------------------------------------

    [BenchmarkCategory("ReadModify"), Benchmark(Baseline = true, Description = "Raw: Find + ReplaceOne (Bulk)")]
    public async Task ReadAndModify_RawDriver_Replace_NoConcurrency()
    {
        var filter = Builders<BenchmarkEntity>.Filter.In(e => e.Id, _testIds);
        var entities = await _collection.Find(filter).ToListAsync();

        var writes = entities.Select(e =>
        {
            e.Age++;
            e.Name = "Updated Name";
            // Polymorphism check
            if (e.PolymorphicData is DerivedDataB b) b.InfoB++;

            return new ReplaceOneModel<BenchmarkEntity>(
                Builders<BenchmarkEntity>.Filter.Eq(x => x.Id, e.Id), e);
        }).ToList<WriteModel<BenchmarkEntity>>();

        await _collection.BulkWriteAsync(writes);
    }

    [BenchmarkCategory("ReadModify"), Benchmark(Description = "Raw: Find + ReplaceOne (Manual Concurrency)")]
    public async Task ReadAndModify_RawDriver_Replace_ManualConcurrency()
    {
        var filter = Builders<BenchmarkEntity>.Filter.In(e => e.Id, _testIds);
        var entities = await _collection.Find(filter).ToListAsync();

        var writes = entities.Select(e =>
        {
            var oldVersion = e.Version;
            e.Age++;
            e.Name = "Updated Name";
            e.Version++;
            if (e.PolymorphicData is DerivedDataB b) b.InfoB++;
            
            var updateFilter = Builders<BenchmarkEntity>.Filter.And(
                Builders<BenchmarkEntity>.Filter.Eq(x => x.Id, e.Id),
                Builders<BenchmarkEntity>.Filter.Eq(x => x.Version, oldVersion)
            );
            
            return new ReplaceOneModel<BenchmarkEntity>(updateFilter, e) { IsUpsert = false };
        }).ToList<WriteModel<BenchmarkEntity>>();

        var result = await _collection.BulkWriteAsync(writes);
        if (result.MatchedCount < entities.Count)
        {
            throw new Exception("Concurrency conflict");
        }
    }

    [BenchmarkCategory("ReadModify"), Benchmark(Description = "Raw: Find + UpdateOne.Set (Bulk)")]
    public async Task ReadAndModify_RawDriver_Set_NoConcurrency()
    {
        var filter = Builders<BenchmarkEntity>.Filter.In(e => e.Id, _testIds);
        var entities = await _collection.Find(filter).ToListAsync();

        var writes = entities.Select(e =>
        {
            e.Age++;
            e.Name = "Updated Name";
            
            var update = Builders<BenchmarkEntity>.Update
                .Inc(x => x.Age, 1)
                .Set(x => x.Name, "Updated Name");

            if (e.PolymorphicData is DerivedDataB b)
            {
                b.InfoB++;
                update = update.Set("PolymorphicData.InfoB", b.InfoB);
            }

            return new UpdateOneModel<BenchmarkEntity>(
                Builders<BenchmarkEntity>.Filter.Eq(x => x.Id, e.Id), update);
        }).ToList<WriteModel<BenchmarkEntity>>();

        await _collection.BulkWriteAsync(writes);
    }

    [BenchmarkCategory("ReadModify"), Benchmark(Description = "Raw: Find + UpdateOne.Set (Manual Concurrency)")]
    public async Task ReadAndModify_RawDriver_Set_ManualConcurrency()
    {
        var filter = Builders<BenchmarkEntity>.Filter.In(e => e.Id, _testIds);
        var entities = await _collection.Find(filter).ToListAsync();

        var writes = entities.Select(e =>
        {
            var oldVersion = e.Version;
            e.Age++;
            e.Name = "Updated Name";
            e.Version++;

            var update = Builders<BenchmarkEntity>.Update
                .Inc(x => x.Age, 1)
                .Set(x => x.Name, "Updated Name")
                .Inc(x => x.Version, 1);

            if (e.PolymorphicData is DerivedDataB b)
            {
                b.InfoB++;
                update = update.Set("PolymorphicData.InfoB", b.InfoB);
            }
            
            var updateFilter = Builders<BenchmarkEntity>.Filter.And(
                Builders<BenchmarkEntity>.Filter.Eq(x => x.Id, e.Id),
                Builders<BenchmarkEntity>.Filter.Eq(x => x.Version, oldVersion)
            );
            
            return new UpdateOneModel<BenchmarkEntity>(updateFilter, update);
        }).ToList<WriteModel<BenchmarkEntity>>();

        var result = await _collection.BulkWriteAsync(writes);
        if (result.MatchedCount < entities.Count)
        {
            throw new Exception("Concurrency conflict");
        }
    }

    [BenchmarkCategory("ReadModify"), Benchmark(Description = "Zen: Query + Auto-Shadow + SaveChangesAsync (Concurrency ON)")]
    public async Task ReadAndModify_MongoZen_Set_OptimisticConcurrency()
    {
        await using var session = await BenchmarkDbContextSession.OpenSessionAsync(_dbContext);
        var entities = await session.Entities.QueryAsync(e => _testIds.Contains(e.Id));
        foreach (var entity in entities)
        {
            entity.Age++;
            entity.Name = "Updated Name";
            if (entity.PolymorphicData is DerivedDataB b) b.InfoB++;
        }
        await session.SaveChangesAsync();
    }

    [BenchmarkCategory("ReadModify"), Benchmark(Description = "Zen: Query + Auto-Shadow + SaveChangesAsync (Concurrency OFF)")]
    public async Task ReadAndModify_MongoZen_Set_NoConcurrency()
    {
        var options = new DbContextOptions(_database, new Conventions { ConcurrencyPropertyName = null });
        var db = new BenchmarkDbContext(options);
        await using var session = await BenchmarkDbContextSession.OpenSessionAsync(db);
        var entities = await session.Entities.QueryAsync(e => _testIds.Contains(e.Id));
        foreach (var entity in entities)
        {
            entity.Age++;
            entity.Name = "Updated Name";
            if (entity.PolymorphicData is DerivedDataB b) b.InfoB++;
        }
        await session.SaveChangesAsync();
    }

    // -------------------------------------------------------------------------
    // Identity Map / repeated-read benchmarks
    // -------------------------------------------------------------------------

    [BenchmarkCategory("IdentityMap"), Benchmark(Baseline = true, Description = "Raw: Load 100x (No Tracking)")]
    public async Task IdentityMap_RawDriver_NoTracking()
    {
        var id = _testIds[0];
        for (int i = 0; i < 100; i++)
        {
            await _collection.Find(e => e.Id == id).SingleAsync();
        }
    }

    [BenchmarkCategory("IdentityMap"), Benchmark(Description = "Zen: Load 100x (From Identity Map)")]
    public async Task IdentityMap_MongoZen_FromMemory()
    {
        var id = _testIds[0];
        await using var session = await BenchmarkDbContextSession.OpenSessionAsync(_dbContext);
        for (int i = 0; i < 100; i++)
        {
            await session.Entities.LoadAsync(id);
        }
    }

    // -------------------------------------------------------------------------
    // Attachments (GridFS) benchmarks
    // -------------------------------------------------------------------------

    [BenchmarkCategory("Attachments"), Benchmark(Baseline = true, Description = "Raw: GridFSBucket Upload + Download (1MB)")]
    public async Task Attachments_RawDriver_GridFSBucket()
    {
        var id = "bench/raw";
        using (var stream = new MemoryStream(_attachmentData))
        {
            await _bucket.UploadFromStreamAsync(id, stream);
        }

        using (var dest = new MemoryStream())
        {
            await _bucket.DownloadToStreamByNameAsync(id, dest);
        }
    }

    [BenchmarkCategory("Attachments"), Benchmark(Description = "Zen: Attachments.Store + Get (1MB, Optimized)")]
    public async Task Attachments_MongoZen_Optimized()
    {
        await using var session = await BenchmarkDbContextSession.OpenSessionAsync(_dbContext);
        var entityId = "bench/zen";
        var name = "file.bin";

        using (var stream = new MemoryStream(_attachmentData))
        {
            await session.Attachments.StoreAsync(entityId, name, stream);
        }

        using (var downloadStream = await session.Attachments.GetAsync(entityId, name))
        {
            var buffer = new byte[8192];
            while (await downloadStream.ReadAsync(buffer, 0, buffer.Length) > 0)
            {
                // Simulate processing
            }
        }
    }
}
