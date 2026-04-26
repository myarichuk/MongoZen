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

## Optimistic Concurrency

MongoZen supports optimistic concurrency out of the box. This prevents "last-write-wins" scenarios where multiple users might overwrite each other's changes concurrently.

### How it works

1.  **Concurrency Token**: By default, MongoZen looks for a property named `Version` (configurable via `Conventions.ConcurrencyPropertyName`).
2.  **Automatic Tracking**: When an entity is loaded, its version is tracked in the session.
3.  **Atomic Updates**: When `SaveChangesAsync()` is called, MongoZen includes the expected version in the update filter:
    `{ _id: "doc-id", Version: 1 }`
4.  **Automatic Increment**: If the update succeeds, the version is automatically incremented in the database and in your local POCO.
5.  **Conflict Detection**: If another process modified the document (changing its version), the update filter won't match. MongoZen detects this mismatch, identifies the conflicting documents, and throws a `ConcurrencyException`.

### Transactional Guarantees

*   **Replica Sets / Sharded Clusters**: MongoZen uses native MongoDB transactions by default. If a single document in a bulk operation fails a concurrency check, the **entire session is rolled back**, ensuring your database never ends up in a partially-applied state.
*   **Standalone Nodes**: If transactions are not supported, MongoZen still enforces the version check, but a failure may result in a partial save. We strongly recommend running a single-node replica set even for local development.

```csharp
try 
{
    await session.SaveChangesAsync();
}
catch (ConcurrencyException ex)
{
    // ex.FailedIds contains the IDs of the documents that caused the conflict
    foreach (var id in ex.FailedIds) { ... }
}
```

## Performance & Benchmarks

We compare MongoZen against a **hand-optimized raw driver** baseline. The goal isn't just to be "as fast as" the driver; it's to prove that the architectural overhead of Change Tracking and Identity Maps is negligible—or even beneficial—compared to manual boilerplate.

### Results (1,000 Entities)

*Test Environment: .NET 10, MongoDB Replica Set in Docker (directConnection=true).*

| Method | Category | Count | Mean | Ratio | Allocated | Alloc Ratio |
| :--- | :--- | :--- | :--- | :--- | :--- | :--- |
| **MongoZen_ReadRepeat** | **IdentityMap** | 1000 | **2.3 ms** | **0.01** | **33 KB** | **0.01** |
| RawDriver_ReadRepeat | IdentityMap | 1000 | 341.7 ms | 1.00 | 3173 KB | 1.00 |
| **MongoZen_ReadAndModify** | **ReadModify** | 1000 | **59.9 ms** | **0.77** | **6032 KB** | **0.94** |
| RawDriver_ReadAndModify | ReadModify | 1000 | 88.2 ms | 1.00 | 6443 KB | 1.00 |
| **MongoZen_InsertBatch** | **Insert** | 1000 | **44.0 ms** | **1.21** | **2302 KB** | **1.40** |
| RawDriver_InsertBatch | Insert | 1000 | 37.5 ms | 1.00 | 1647 KB | 1.00 |

### What these numbers mean:

1.  **IdentityMap (Repeated Reads)**: Serving data from memory is always faster than hitting the wire. MongoZen is **~100x faster** for repeated loads because it bypasses the network and serialization entirely. If you have the data in the session, why go back to the source?
2.  **ReadAndModify (The "Zen" Win)**: Even with the overhead of change tracking, MongoZen is **23% faster** than hand-written `BulkWrite` code at scale. Our internal engine is simply more efficient at preparing write models than manual C# code. This is where the architectural investment pays off.
3.  **Insert (The "Tracking Tax")**: There is a modest **21% overhead** on inserts. This is the one-time cost of initializing tracking for the entity lifecycle. You pay a small fee at the door to enjoy the massive performance gains and "no-nonsense" API for the rest of the session.

## More Info

Check out our [Wiki](https://github.com/myarichuk/MongoZen/wiki) for:
*   [Detailed How-Tos](https://github.com/myarichuk/MongoZen/wiki/Getting-Started)
*   [Testing Strategies](https://github.com/myarichuk/MongoZen/wiki/Testing-With-InMemoryDbSet)
*   [Under the Hood: LINQ Translation](https://github.com/myarichuk/MongoZen/wiki/Under-The-Hood-LINQ-And-Include)

## License

MIT. Go build something cool.
