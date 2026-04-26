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
| **MongoZen_ReadRepeat** | **IdentityMap** | 1000 | **7.3 ms** | **0.02** | **31 KB** | **0.01** |
| RawDriver_ReadRepeat | IdentityMap | 1000 | 307.2 ms | 1.00 | 3175 KB | 1.00 |
| **MongoZen_ReadAndModify** | **ReadModify** | 1000 | **195.3 ms** | **1.45** | **7305 KB** | **1.12** |
| **MongoZen_ReadAndModify_NoConcurrency** | **ReadModify** | 1000 | **106.1 ms** | **0.79** | **6084 KB** | **0.94** |
| RawDriver_ReadAndModify | ReadModify | 1000 | 134.1 ms | 1.00 | 6499 KB | 1.00 |
| RawDriver_ReadAndModify_WithManualConcurrency | ReadModify | 1000 | 365.4 ms | 2.72 | 8890 KB | 1.37 |
| **MongoZen_InsertBatch** | **Insert** | 1000 | **166.7 ms** | **2.82** | **2334 KB** | **1.40** |
| **MongoZen_InsertBatch_NoConcurrency** | **Insert** | 1000 | **68.4 ms** | **1.16** | **2339 KB** | **1.40** |
| RawDriver_InsertBatch | Insert | 1000 | 59.1 ms | 1.00 | 1669 KB | 1.00 |

### What these numbers mean:

1.  **IdentityMap (Repeated Reads)**: Serving data from memory is always faster than hitting the wire. MongoZen is **~50x to 100x faster** for repeated loads because it bypasses the network and serialization entirely. If you have the data in the session, why go back to the source?
2.  **ReadAndModify (Concurrency Efficiency)**: MongoZen's automated optimistic concurrency (195.3ms) is **nearly 2x faster** than a hand-written manual concurrency implementation using the raw driver (365.4ms). Our internal diffing and batching engine handles the complexity more efficiently than manual boilerplate. Without concurrency enabled, MongoZen is even faster than the non-concurrent raw driver (106.1ms vs 134.1ms) at scale, proving the efficiency of the "Zen" architecture.
3.  **Insert (The "Tracking Tax")**: There is an overhead on inserts when concurrency tracking is enabled, primarily due to the initial setup of the versioning metadata. However, for non-versioned entities, the overhead is minimal (68ms vs 59ms). Since inserts are typically less frequent than read/modify cycles, and we lazily resolve tracking components, this is a highly favorable trade-off for the performance and safety gains achieved during the rest of the entity lifecycle.

## More Info

Check out our [Wiki](https://github.com/myarichuk/MongoZen/wiki) for:
*   [Detailed How-Tos](https://github.com/myarichuk/MongoZen/wiki/Getting-Started)
*   [Testing Strategies](https://github.com/myarichuk/MongoZen/wiki/Testing-With-InMemoryDbSet)
*   [Under the Hood: LINQ Translation](https://github.com/myarichuk/MongoZen/wiki/Under-The-Hood-LINQ-And-Include)

## License

MIT. Go build something cool.
