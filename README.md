# MongoZen

Look, MongoDB is great, but the mapping experience in C# usually sucks. You either end up with reflection-heavy "automagical" libraries that crawl at runtime, or you're stuck writing manual BsonDocument boilerplate like it's 2011.

**MongoZen** is what happens when you want the "Unit of Work" and "Identity Map" patterns from EF Core or RavenDB, but you want them to actually be fast and MongoDB-native. No nonsense, just performance.

## Why use this?

*   **No Reflection on the Hot Path**: We use Roslyn Source Generators to wire up your `DbSet` and sessions at compile-time. If it's slow, it's not because of us.
*   **Identity Map**: If you load the same document twice in one session, you get the same instance. Period.
*   **Automatic Change Tracking**: Modify your POCOs directly. When you call `SaveChangesAsync()`, we figure out what changed and flush it in a **single bulk operation** per collection.
*   **RavenDB-style API**: `Store`, `Delete`, `LoadAsync`. It's a clean API that doesn't get in your way.
*   **In-Memory Provider**: Write tests that actually run fast without spinning up a Docker container every time.

## Quick Start

### 1. Define your Context

You need a `partial` class so the generator can do its thing.

```csharp
public partial class MyDbContext : MongoZen.DbContext
{
    // These properties are automatically initialized
    public IDbSet<Person> People { get; set; } = null!;

    public MyDbContext(DbContextOptions options) : base(options) { }
}
```

### 2. Use it

Everything happens inside a session. 

```csharp
var options = DbContextOptions.CreateForMongo("mongodb://localhost:27017", "MyDatabase");
var db = new MyDbContext(options);

await using var session = db.StartSession();

// Fetch Alice
var alice = await session.LoadAsync<Person>("alice-id");

// Just change the property. No .Update() needed.
alice.Age = 31;

// Load someone else while we're at it
var bob = new Person { Name = "Bob", Age = 25 };
session.Store(bob);

// One network round-trip to commit everything
await session.SaveChangesAsync();
```

## Testing

Just swap the options. It's that simple.

```csharp
var options = new DbContextOptions(); // Default is In-Memory
var testDb = new MyDbContext(options);
```

## More Info

Check out our [Wiki](https://github.com/myarichuk/MongoZen/wiki) for:
*   [Detailed How-Tos](https://github.com/myarichuk/MongoZen/wiki/Getting-Started)
*   [Testing Strategies](https://github.com/myarichuk/MongoZen/wiki/Testing-With-InMemoryDbSet)
*   [Under the Hood: LINQ Translation](https://github.com/myarichuk/MongoZen/wiki/Under-The-Hood-LINQ-And-Include)

## License

MIT. Go build something cool.
