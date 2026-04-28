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

| Method | Category | Count | Mean | Ratio | Allocated |
| :--- | :--- | :--- | :--- | :--- | :--- |
| **IdentityMap_MongoZen_FromMemory** | **IdentityMap** | 1000 | **3.4 ms** | **0.01** | **36 KB** |
| IdentityMap_RawDriver_NoTracking | IdentityMap | 1000 | 477.1 ms | 1.00 | 3244 KB |
| **ReadModify_MongoZen_Set_OptimisticConcurrency** | **ReadModify** | 1000 | **260.9 ms** | **2.36** | **14548 KB** |
| **ReadModify_MongoZen_Set_NoConcurrency** | **ReadModify** | 1000 | **113.6 ms** | **1.03** | **6409 KB** |
| ReadModify_RawDriver_Replace_Bulk | ReadModify | 1000 | 115.7 ms | 1.05 | 7204 KB |
| ReadModify_RawDriver_Replace_ManualConcurrency | ReadModify | 1000 | 444.8 ms | 4.02 | 9603 KB |
| ReadModify_RawDriver_Set_Bulk | ReadModify | 1000 | 111.3 ms | 1.01 | 11333 KB |
| ReadModify_RawDriver_Set_ManualConcurrency | ReadModify | 1000 | 461.3 ms | 4.17 | 16357 KB |
| **Insert_MongoZen_OptimisticConcurrency** | **Insert** | 1000 | **42.7 ms** | **0.87** | **1901 KB** |
| **Insert_MongoZen_NoConcurrency** | **Insert** | 1000 | **47.7 ms** | **0.97** | **1928 KB** |
| Insert_RawDriver_Bulk | Insert | 1000 | 50.5 ms | 1.00 | 1784 KB |
| **Attachments_MongoZen_Optimized (1MB)** | **Attachments** | 1000 | **167.0 ms** | **1.87** | **2430 KB** |
| Attachments_RawDriver_GridFS (1MB) | Attachments | 1000 | 91.9 ms | 1.00 | 6790 KB |

### What these numbers mean:

1.  **IdentityMap (Repeated Reads)**: Serving data from memory is always faster than hitting the wire. MongoZen is **~100x faster** for repeated loads because it bypasses the network and serialization entirely.
2.  **ReadAndModify (Implicit vs. Manual Optimization)**: 
    *   **The "Convenience" Comparison**: Compare `ReadModify_RawDriver_Replace_Bulk` (116ms) with `ReadModify_MongoZen_Set_NoConcurrency` (114ms). MongoZen performs identically to bulk replacement while providing the convenience of an identity map and unit of work.
    *   **The "Expert" Comparison**: Compare `ReadModify_RawDriver_Set_ManualConcurrency` (461ms) with `ReadModify_MongoZen_Set_OptimisticConcurrency` (261ms). Even with the overhead of automated change tracking, MongoZen is **nearly 2x faster** than manually implementing a concurrency-safe bulk update using the raw driver. Our internal batching and unmanaged diffing engine out-performs typical manual boilerplate.
3.  **Insert (Scale Efficiency)**: At 1,000 entities, MongoZen is actually **slightly faster** than the raw driver's `InsertManyAsync`. The overhead of setting up tracking is amortized over the batch, and our optimized commit pipeline ensures minimal overhead.
4.  **Attachments (Memory & Throughput Efficiency)**: Zen uses **~65% less memory** than the raw GridFS bucket for attachments. By implementing chunk batching (`InsertManyAsync`) and pooling the underlying `ArenaAllocator`, we've optimized both throughput and stability for high-scale attachment operations.
5.  **Allocations**: Our recent optimizations have drastically reduced GC pressure by using single-pass diffing and direct `BsonDocument` builders for updates, ensuring that Zen remains the most memory-efficient way to track changes in MongoDB.

## More Info

Check out our [Wiki](https://github.com/myarichuk/MongoZen/wiki) for:
*   [Detailed How-Tos](https://github.com/myarichuk/MongoZen/wiki/Getting-Started)
*   [Testing Strategies](https://github.com/myarichuk/MongoZen/wiki/Testing-With-InMemoryDbSet)
*   [Under the Hood: LINQ Translation](https://github.com/myarichuk/MongoZen/wiki/Under-The-Hood-LINQ-And-Include)

## License

MIT. Go build something cool.
