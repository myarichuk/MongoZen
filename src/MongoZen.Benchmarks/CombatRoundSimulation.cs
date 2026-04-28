using BenchmarkDotNet.Attributes;
using MongoDB.Driver;
using MongoZen;

namespace MongoZen.Benchmarks;

[MemoryDiagnoser]
public class CombatRoundSimulation
{
    private BenchmarkDbContext _dbContext = null!;
    private List<BenchmarkEntity> _characters = null!;
    private List<string> _charIds = null!;

    [Params(100, 1000)]
    public int CharacterCount;

    [Params(20)]
    public int ActionsPerRound;

    [GlobalSetup]
    public async Task Setup()
    {
        var options = new DbContextOptions(null!) { UseInMemory = true };
        _dbContext = new BenchmarkDbContext(options);

        _characters = Enumerable.Range(0, CharacterCount).Select(i => new BenchmarkEntity
        {
            Id = $"char/{i}",
            Name = $"Hero {i}",
            Age = 25,
            Version = 1
        }).ToList();
        _charIds = _characters.Select(c => c.Id).ToList();
        
        // Populate the "Database" (InMemoryDbSet) using a session
        await using var session = await BenchmarkDbContextSession.OpenSessionAsync(_dbContext, startTransaction: false);
        foreach(var c in _characters)
        {
            session.Store(c);
        }
        await session.SaveChangesAsync();
    }

    [Benchmark]
    public async Task SimulateRound_LongLivedSession()
    {
        var random = new Random(42);
        
        await using var session = await BenchmarkDbContextSession.OpenSessionAsync(_dbContext, startTransaction: false);
        
        for (int i = 0; i < ActionsPerRound; i++)
        {
            var actorId = _charIds[random.Next(CharacterCount)];
            var targetIds = Enumerable.Range(0, 3).Select(_ => _charIds[random.Next(CharacterCount)]).ToList();

            var actor = await session.Entities.LoadAsync(actorId);
            if (actor != null)
            {
                actor.Age++;
            }

            foreach (var tId in targetIds)
            {
                var target = await session.Entities.LoadAsync(tId);
                if (target != null)
                {
                    target.Age--;
                }
            }

            await session.SaveChangesAsync();
        }
    }

    private async Task PerformAction(string actorId, List<string> targetIds)
    {
        await using var session = await BenchmarkDbContextSession.OpenSessionAsync(_dbContext, startTransaction: false);
        
        var actor = await session.Entities.LoadAsync(actorId);
        if (actor != null)
        {
            actor.Age++; // Character level up or something
        }

        foreach (var tId in targetIds)
        {
            var target = await session.Entities.LoadAsync(tId);
            if (target != null)
            {
                target.Age--; // Taking damage
            }
        }

        await session.SaveChangesAsync();
    }
}
