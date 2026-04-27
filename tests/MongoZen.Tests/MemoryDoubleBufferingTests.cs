using MongoZen.Collections;
using SharpArena.Allocators;
using Xunit;
using System;
using System.Threading.Tasks;

namespace MongoZen.Tests;

public class MemoryDoubleBufferingTests : IntegrationTestBase
{
    public class SimpleEntity
    {
        public string Id { get; set; } = null!;
        public string Data { get; set; } = null!;
    }

    private struct SimpleEntity_Shadow
    {
        public bool _hasValue;
        public ArenaString Data;

        public void From(SimpleEntity source, ArenaAllocator arena)
        {
            _hasValue = true;
            Data = ArenaString.Clone(source.Data, arena);
        }

        public bool IsDirty(SimpleEntity current)
        {
            if (current == null) return _hasValue;
            if (!_hasValue) return true;
            return !Data.Equals(current.Data);
        }
    }

    private class TestDbContext(DbContextOptions options) : DbContext(options)
    {
        public IDbSet<SimpleEntity> Entities { get; set; } = null!;
    }

    private class TestSession : DbContextSession<TestDbContext>
    {
        public static async Task<TestSession> OpenSessionAsync(TestDbContext dbContext)
        {
            var session = new TestSession(dbContext);
            await session.Advanced.InitializeAsync();
            return session;
        }

        private TestSession(TestDbContext dbContext) : base(dbContext)
        {
            Entities = new MutableDbSet<SimpleEntity>(
                _dbContext.Entities,
                () => Transaction,
                this,
                (e, a) => { 
                    unsafe {
                        var ptr = a.Alloc((nuint)System.Runtime.CompilerServices.Unsafe.SizeOf<SimpleEntity_Shadow>());
                        ref var s = ref System.Runtime.CompilerServices.Unsafe.AsRef<SimpleEntity_Shadow>(ptr);
                        s.From(e, a);
                        return (IntPtr)ptr;
                    }
                },
                (e, ptr) => { unsafe { ref var s = ref System.Runtime.CompilerServices.Unsafe.AsRef<SimpleEntity_Shadow>((void*)ptr); return s.IsDirty(e); } },
                null,
                _dbContext.Options.Conventions);
            RegisterDbSet((MutableDbSet<SimpleEntity>)Entities);
        }

        public IMutableDbSet<SimpleEntity> Entities { get; }
    }

    [Fact]
    public async Task SaveChangesAsync_DoesNotLeakArenaMemory()
    {
        var db = new TestDbContext(new DbContextOptions(Database!) { UseInMemory = true });
        var entity = new SimpleEntity { Id = "m1", Data = new string('A', 1000) };
        ((InMemoryDbSet<SimpleEntity>)db.Entities).Seed(entity); // Use Seed to bypass tracking logic for initial data

        await using (var session = await TestSession.OpenSessionAsync(db))
        {
            // Load and track
            var loaded = await session.Entities.LoadAsync("m1");
            Assert.NotNull(loaded);
            
            // Perform many save cycles
            for (int i = 0; i < 100; i++)
            {
                loaded!.Data = "Change " + i;
                await session.SaveChangesAsync();

                Assert.NotNull(session);
                var arena = session.Arena;
                Assert.NotNull(arena);

                // Assert that the arena isn't growing indefinitely.
                Assert.True(arena.AllocatedBytes < 20000, $"Memory leak detected! Allocated: {arena.AllocatedBytes} bytes at iteration {i}");
            }
        }
    }
}
