using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using MongoDB.Bson;
using MongoDB.Driver;
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
    private BenchmarkDbContext _dbContext = null!;
    private List<BenchmarkEntity> _testData = null!;
    private List<string> _testIds = null!;

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

    // -------------------------------------------------------------------------
    // Insert benchmarks
    // -------------------------------------------------------------------------

    [BenchmarkCategory("Insert"), Benchmark(Baseline = true)]
    public async Task Insert_RawDriver_Bulk()
    {
        await _collection.InsertManyAsync(_testData);
    }

    [BenchmarkCategory("Insert"), Benchmark]
    public async Task Insert_MongoZen_OptimisticConcurrency()
    {
        await using var session = _dbContext.StartSession();
        foreach (var entity in _testData)
        {
            session.Store(entity);
        }
        await session.SaveChangesAsync();
    }

    [BenchmarkCategory("Insert"), Benchmark]
    public async Task Insert_MongoZen_NoConcurrency()
    {
        var options = new DbContextOptions(_database, new Conventions { ConcurrencyPropertyName = null });
        var db = new BenchmarkDbContext(options);
        await using var session = db.StartSession();
        foreach (var entity in _testData)
        {
            session.Store(entity);
        }
        await session.SaveChangesAsync();
    }

    // -------------------------------------------------------------------------
    // Read + modify benchmarks
    // -------------------------------------------------------------------------

    [BenchmarkCategory("ReadModify"), Benchmark(Baseline = true)]
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

    [BenchmarkCategory("ReadModify"), Benchmark]
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

    [BenchmarkCategory("ReadModify"), Benchmark]
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

    [BenchmarkCategory("ReadModify"), Benchmark]
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

    [BenchmarkCategory("ReadModify"), Benchmark]
    public async Task ReadAndModify_MongoZen_Set_OptimisticConcurrency()
    {
        await using var session = _dbContext.StartSession();
        var entities = await session.Entities.QueryAsync(e => _testIds.Contains(e.Id));
        foreach (var entity in entities)
        {
            entity.Age++;
            entity.Name = "Updated Name";
            if (entity.PolymorphicData is DerivedDataB b) b.InfoB++;
        }
        await session.SaveChangesAsync();
    }

    [BenchmarkCategory("ReadModify"), Benchmark]
    public async Task ReadAndModify_MongoZen_Set_NoConcurrency()
    {
        var options = new DbContextOptions(_database, new Conventions { ConcurrencyPropertyName = null });
        var db = new BenchmarkDbContext(options);
        await using var session = db.StartSession();
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

    [BenchmarkCategory("IdentityMap"), Benchmark(Baseline = true)]
    public async Task IdentityMap_RawDriver_NoTracking()
    {
        var id = _testIds[0];
        for (int i = 0; i < 100; i++)
        {
            await _collection.Find(e => e.Id == id).SingleAsync();
        }
    }

    [BenchmarkCategory("IdentityMap"), Benchmark]
    public async Task IdentityMap_MongoZen_FromMemory()
    {
        var id = _testIds[0];
        await using var session = _dbContext.StartSession();
        for (int i = 0; i < 100; i++)
        {
            await session.Entities.LoadAsync(id);
        }
    }
}
