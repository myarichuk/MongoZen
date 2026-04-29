using System.Collections.Generic;
using System.Threading.Tasks;
using MongoDB.Driver;
using Xunit;
using MongoDB.Bson.Serialization.Attributes;

namespace MongoZen.Tests;

public class PolymorphismTests : IntegrationTestBase
{
    [BsonKnownTypes(typeof(Dog), typeof(Cat))]
    public abstract class Animal
    {
        public string Name { get; set; } = null!;
    }

    public class Dog : Animal
    {
        public int BarkVolume { get; set; }
    }

    public class Cat : Animal
    {
        public bool IsGrumpy { get; set; }
    }

    public class Zoo
    {
        public string Id { get; set; } = null!;
        public Animal MainAttraction { get; set; } = null!;
        public List<Animal> Animals { get; set; } = new();
    }

    private class TestDbContext(DbContextOptions options) : DbContext(options)
    {
        public IDbSet<Zoo> Zoos { get; set; } = null!;

        protected override void InitializeDbSets()
        {
            if (Options.UseInMemory)
                Zoos = new InMemoryDbSet<Zoo>("Zoos", Options.Conventions);
            else
                Zoos = new DbSet<Zoo>(Options.Mongo!.GetCollection<Zoo>("Zoos"), Options.Conventions);
        }

        public override string GetCollectionName(Type entityType)
        {
            if (entityType == typeof(Zoo)) return "Zoos";
            throw new ArgumentException();
        }
    }

    private class TestSession : DbContextSession<TestDbContext>
    {
        public static async Task<TestSession> OpenSessionAsync(TestDbContext dbContext)
        {
            var session = new TestSession(dbContext);
            await session.Advanced.InitializeAsync();
            return session;
        }

        private TestSession(TestDbContext db) : base(db)
        {
            Zoos = new MutableDbSet<Zoo>(
                db.Zoos,
                () => Transaction,
                this,
                (e, a) => (IntPtr)1,   // Mock tracker (must be non-zero)
                (e, p) => true,        // Always dirty
                (e, p) => null,        // Full replace
                db.Options.Conventions
            );
            RegisterDbSet((MutableDbSet<Zoo>)Zoos);
        }
        public IDbSet<Zoo> Zoos { get; }
    }

    [Fact]
    public async Task Update_DerivedProperty_IsTracked()
    {
        var db = new TestDbContext(new DbContextOptions(Database!));
        var collection = Database!.GetCollection<Zoo>("Zoos");

        var zoo = new Zoo
        {
            Id = "z1",
            MainAttraction = new Dog { Name = "Rex", BarkVolume = 10 },
            Animals = new List<Animal> { new Cat { Name = "Fluffy", IsGrumpy = true } }
        };
        await collection.InsertOneAsync(zoo);

        await using (var session = await TestSession.OpenSessionAsync(db))
        {
            var loaded = await session.Zoos.LoadAsync("z1");
            Assert.NotNull(loaded);
            
            // Modify a derived property
            var dog = Assert.IsType<Dog>(loaded!.MainAttraction);
            dog.BarkVolume = 100;
            
            // Modify an element in the collection
            var cat = Assert.IsType<Cat>(loaded.Animals[0]);
            cat.IsGrumpy = false;

            await session.SaveChangesAsync();
        }

        var fresh = await collection.Find(e => e.Id == "z1").FirstAsync();
        var freshDog = Assert.IsType<Dog>(fresh.MainAttraction);
        Assert.Equal(100, freshDog.BarkVolume);
        
        var freshCat = Assert.IsType<Cat>(fresh.Animals[0]);
        Assert.False(freshCat.IsGrumpy);
    }
}
