# MongoZen

MongoDB is nice and all, but the driver experience in C# usually sucks. You either end up with reflection-heavy "automagical" repositories, or you're writing manual BsonDocument boilerplate for aggregation pipelines like it's 2011.

Now, the idea behind **MongoZen** is to take a Mongo driver then add "Unit of Work" and "Identity Map" patterns from EF Core or RavenDB. But I wanted them to actually be fast and as MongoDB-native as possible.

## So, why should you care?

*   **No Reflection on the Hot Path**: Instead, there are Roslyn Source Generators to wire up your `DbSet` and sessions at compile-time. If it's slow, it's not because of us.
*   **Identity Map**: If you load the same document twice in one session, you get the same instance.
*   **Automatic Change Tracking**: Modify POCOs directly. When you call `SaveChangesAsync()`, we figure out what changed and flush it in a **single bulk write operation** per collection. Or a transaction if supported.
*   **RavenDB-inspired API**: `Store`, `Delete`, `LoadAsync`. It's a clean API that doesn't get in your way.
*   **In-Memory Provider**: Write tests that run fast without spinning up a Docker "testcontainer" container every time.

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

## Performance & Benchmarks (WIP)

We compare MongoZen against a **hand-optimized raw driver** baseline. The goal isn't just to be "as fast as" the driver, but to prove that the architectural overhead of Change Tracking and Identity Maps is negligible (or even beneficial) compared to manual boilerplate.

### Results (1,000 & 5,000 Entities)

*Test Environment: .NET 10, MongoDB Replica Set in Docker (directConnection=true).*

| Method | Category | Count | Mean | Ratio | Allocated | Alloc Ratio |
| :--- | :--- | :--- | :--- | :--- | :--- | :--- |
| **MongoZen_ReadRepeat** | **IdentityMap** | 5000 | **2.7 ms** | **0.02** | **32 KB** | **0.01** |
| RawDriver_ReadRepeat | IdentityMap | 5000 | 140.0 ms | 1.00 | 3172 KB | 1.00 |
| **MongoZen_ReadAndModify** | **ReadModify** | 5000 | **232.8 ms** | **0.65** | **31123 KB** | **0.96** |
| RawDriver_ReadAndModify | ReadModify | 5000 | 362.3 ms | 1.00 | 32338 KB | 1.00 |
| **MongoZen_InsertBatch** | **Insert** | 5000 | **397.0 ms** | **3.62** | **20505 KB** | **2.48** |
| RawDriver_InsertBatch | Insert | 5000 | 115.8 ms | 1.00 | 8274 KB | 1.00 |

### What these numbers mean:

1.  **IdentityMap (Repeated Reads)**: Serve requests from memory. MongoZen is **~50x - 100x faster** because it serves repeated requests for the same ID from the local `ISessionTracker` instead of hitting the wire.
2.  **ReadAndModify (Change Tracking)**: This is the core "Zen" win. Even with the overhead of diffing, MongoZen is **35% faster** and uses **less memory** than hand-written `BulkWrite` code. Why? Our optimized internal engine (using unmanaged Arena memory and HashSets) is more efficient at preparing the write models than manual LINQ-to-model mapping.
3.  **Insert (The "Tracking Tax")**: This is the only place with overhead (~3.6x slower). This is the one-time cost of allocating shadow structs and registering entities in the Identity Map so you can enjoy the performance wins above for the rest of the object lifecycle.

## More Info

Check out our [Wiki](https://github.com/myarichuk/MongoZen/wiki) for:
*   [Detailed How-Tos](https://github.com/myarichuk/MongoZen/wiki/Getting-Started)
*   [Testing Strategies](https://github.com/myarichuk/MongoZen/wiki/Testing-With-InMemoryDbSet)
*   [Under the Hood: LINQ Translation](https://github.com/myarichuk/MongoZen/wiki/Under-The-Hood-LINQ-And-Include)

## License

MIT. Go build something cool.
