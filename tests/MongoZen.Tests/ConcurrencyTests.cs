using MongoDB.Driver;
using Xunit;
using MongoZen;
using SharpArena.Allocators;

namespace MongoZen.Tests;

public class Person
{
    public string Id { get; set; } = null!;
    public string Name { get; set; } = null!;
    public int Version { get; set; }
}

public class ConcurrencyTests : IntegrationTestBase
{
    private struct Person_Shadow
    {
        public bool _hasValue;
        public SharpArena.Collections.ArenaString Id;
        public SharpArena.Collections.ArenaString Name;
        public long Version;

        public void From(Person source, ArenaAllocator arena)
        {
            _hasValue = true;
            Id = SharpArena.Collections.ArenaString.Clone(source.Id, arena);
            Name = SharpArena.Collections.ArenaString.Clone(source.Name, arena);
            Version = source.Version;
        }

        public bool IsDirty(Person current)
        {
            if (current == null) return _hasValue;
            if (!_hasValue) return true;
            return !Id.Equals(current.Id) || !Name.Equals(current.Name) || Version != current.Version;
        }
    }

    private class MyDbContext : DbContext
    {
        public IDbSet<Person> People { get; set; } = null!;
        public MyDbContext(DbContextOptions options) : base(options) { }
    }

    private class MyDbContextSession : DbContextSession<MyDbContext>
    {
        public MyDbContextSession(MyDbContext dbContext) : base(dbContext)
        {
            People = new MutableDbSet<Person>(
                _dbContext.People, 
                () => Transaction, 
                this, 
                (entity, arena) => { unsafe {
                    var ptr = arena.Alloc((nuint)System.Runtime.CompilerServices.Unsafe.SizeOf<Person_Shadow>()); 
                    ref var s = ref System.Runtime.CompilerServices.Unsafe.AsRef<Person_Shadow>(ptr); 
                    s.From(entity, arena); 
                    return (System.IntPtr)ptr; 
                } },
                (entity, ptr) => { unsafe {
                    ref var s = ref System.Runtime.CompilerServices.Unsafe.AsRef<Person_Shadow>((void*)ptr); 
                    return s.IsDirty(entity); 
                } },
                _dbContext.Options.Conventions);
            
            RegisterDbSet((MutableDbSet<Person>)People);
        }

        public IMutableDbSet<Person> People { get; }
    }

    [Fact]
    public async Task Should_Throw_ConcurrencyException_On_Conflict()
    {
        var db = new MyDbContext(new DbContextOptions(Database!));

        // 1. Initial setup
        await using (var session = new MyDbContextSession(db))
        {
            session.People.Add(new Person { Id = "p1", Name = "Alice", Version = 1 });
            await session.SaveChangesAsync();
        }

        // 2. Load in two concurrent sessions
        await using var session1 = new MyDbContextSession(db);
        await using var session2 = new MyDbContextSession(db);

        var p1 = await session1.People.LoadAsync("p1");
        var p2 = await session2.People.LoadAsync("p1");

        Assert.NotNull(p1);
        Assert.NotNull(p2);
        Assert.Equal(1, p1.Version);
        Assert.Equal(1, p2.Version);

        // 3. Update session 1 and save
        p1.Name = "Alice Revised";
        await session1.SaveChangesAsync();
        Assert.Equal(2, p1.Version);

        // 4. Update session 2 and try to save - should fail
        p2.Name = "Alice Conflict";
        var ex = await Assert.ThrowsAsync<ConcurrencyException>(() => session2.SaveChangesAsync());
        
        Assert.Single(ex.FailedIds);
        Assert.Equal("p1", ex.FailedIds[0]);
    }
    
    [Fact]
    public async Task Should_Throw_ConcurrencyException_When_Document_Deleted()
    {
        var db = new MyDbContext(new DbContextOptions(Database!));

        // 1. Initial setup
        await using (var session = new MyDbContextSession(db))
        {
            session.People.Add(new Person { Id = "p1", Name = "Alice", Version = 1 });
            await session.SaveChangesAsync();
        }

        // 2. Load in one session
        await using var session1 = new MyDbContextSession(db);
        var p1 = await session1.People.LoadAsync("p1");
        Assert.NotNull(p1);

        // 3. Delete in another session
        await using (var session2 = new MyDbContextSession(db))
        {
            session2.People.Delete("p1");
            await session2.SaveChangesAsync();
        }

        // 4. Try to save session 1 - should fail because document is gone
        p1.Name = "Alice Revised";
        var ex = await Assert.ThrowsAsync<ConcurrencyException>(() => session1.SaveChangesAsync());
        
        Assert.Single(ex.FailedIds);
        Assert.Equal("p1", ex.FailedIds[0]);
    }

    [Fact]
    public async Task Should_Throw_ConcurrencyException_On_Conflict_InMemory()
    {
        var options = new DbContextOptions(); // In-Memory
        var db = new MyDbContext(options);

        // 1. Initial setup
        await using (var session = new MyDbContextSession(db))
        {
            session.People.Add(new Person { Id = "p1", Name = "Alice", Version = 1 });
            await session.SaveChangesAsync();
        }

        // 2. Load in two concurrent sessions
        await using var session1 = new MyDbContextSession(db);
        await using var session2 = new MyDbContextSession(db);

        var p1 = await session1.People.LoadAsync("p1");
        var p2 = await session2.People.LoadAsync("p1");
        
        Assert.NotNull(p1);
        Assert.NotNull(p2);
        
        // 3. Update session 1 and save
        p1.Name = "Alice Revised";
        await session1.SaveChangesAsync();
        Assert.Equal(2, p1.Version);

        // 4. Update session 2 and try to save - should fail because session 2 still thinks it has version 1,
        // but the database (in-memory) now has version 2.
        p2.Name = "Alice Conflict";
        
        // We need to simulate that p2 still has Version 1 in session 2's eyes.
        // In InMemoryDbSet, because it's the SAME reference, p2.Version is already 2!
        // So we manually set it back to 1 to simulate a stale entity.
        p2.Version = 1; 

        var ex = await Assert.ThrowsAsync<ConcurrencyException>(() => session2.SaveChangesAsync());
        Assert.Contains("in-memory", ex.Message);
        Assert.Single(ex.FailedIds);
        Assert.Equal("p1", ex.FailedIds[0]);
    }
}
