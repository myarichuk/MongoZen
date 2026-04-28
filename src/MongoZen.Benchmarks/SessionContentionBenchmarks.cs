using BenchmarkDotNet.Attributes;
using MongoDB.Driver;
using MongoZen;

namespace MongoZen.Benchmarks;

[MemoryDiagnoser]
public class SessionContentionBenchmarks
{
    private BenchmarkDbContext _dbContext = null!;
    private DbContextOptions _options = null!;

    [GlobalSetup]
    public void Setup()
    {
        // We don't need a real MongoDB for this, as we're testing the session infrastructure.
        // Enabling UseInMemory avoids the database configuration check.
        _options = new DbContextOptions(null!) { UseInMemory = true }; 
        _dbContext = new BenchmarkDbContext(_options);
    }

    [Benchmark]
    public async Task SingleThread_OpenClose()
    {
        await using (var session = new FakeSession(_dbContext))
        {
        }
    }

    [Benchmark]
    public async Task Parallel_OpenClose()
    {
        var tasks = new Task[100];
        for (int i = 0; i < 100; i++)
        {
            tasks[i] = Task.Run(async () =>
            {
                await using (var session = new FakeSession(_dbContext))
                {
                }
            });
        }
        await Task.WhenAll(tasks);
    }

    [Benchmark]
    public async Task GeneratedSession_Lazy_OpenClose()
    {
        await using (var session = await BenchmarkDbContextSession.OpenSessionAsync(_dbContext, startTransaction: false))
        {
        }
    }

    [Benchmark]
    public async Task GeneratedSession_Touched_OpenClose()
    {
        await using (var session = await BenchmarkDbContextSession.OpenSessionAsync(_dbContext, startTransaction: false))
        {
            var _ = session.Entities; // Trigger lazy init
        }
    }

    private class RealisticSession : DbContextSession<BenchmarkDbContext>
    {
        public RealisticSession(BenchmarkDbContext dbContext) : base(dbContext, startTransaction: false)
        {
            // This is the OLD eager way, manually simulated
            RegisterDbSet(new MutableDbSet<BenchmarkEntity>(dbContext.Entities, dbContext.Options.Conventions));
        }
    }

    private class FakeSession : DbContextSession<BenchmarkDbContext>
    {
        public FakeSession(BenchmarkDbContext dbContext) : base(dbContext, startTransaction: false)
        {
        }
    }
}
