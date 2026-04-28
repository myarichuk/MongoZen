# MongoZen

MongoDB is nice and all, but the driver experience in C# usually sucks. You either end up with reflection-heavy "automagical" repositories, or you're writing manual BsonDocument boilerplate for aggregation pipelines like it's 2011.

Now, the idea behind **MongoZen** is to take a Mongo driver then add "Unit of Work" and "Identity Map" patterns from EF Core or RavenDB. But I wanted them to actually be fast and as MongoDB-native as possible. It's a lean library designed to stay out of your GC's way while giving you the high-level features you actually need.

## So, why should you care?

*   **No Reflection on the Hot Path**: Instead, there are Roslyn Source Generators to wire up your `DbSet` and sessions at compile-time. If it's slow, it's not because of us.
*   **Identity Map**: If you load the same document twice in one session, you get the same instance.
*   **Automatic Change Tracking**: Modify POCOs directly. When you call `SaveChangesAsync()`, we figure out what changed and flush it in a **single bulk write operation** per collection. Or a transaction if supported.
*   **RavenDB-inspired API**: `Store`, `Delete`, `LoadAsync`. It's a clean API that doesn't get in your way.
*   **In-Memory Provider**: Write tests that run fast without spinning up a Docker "testcontainer" container every time.
*   **Lean & Performance-First**: We aren't making "zero-allocation" marketing claims, but we are religiously focused on minimizing heap churn and maximizing throughput.

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
| **Zen: Load 100x (From Identity Map)** | **IdentityMap** | 1000 | **2.1 ms** | **0.02** | **33 KB** |
| Raw: Load 100x (No Tracking) | IdentityMap | 1000 | 139.9 ms | 1.00 | 3274 KB |
| **Zen: Query + Auto-Shadow + SaveChanges (Concurrency ON)** | **ReadModify** | 1000 | **73.8 ms** | **0.91** | **7795 KB** |
| **Zen: Query + Auto-Shadow + SaveChanges (Concurrency OFF)** | **ReadModify** | 1000 | **81.0 ms** | **1.00** | **6404 KB** |
| Raw: Find + ReplaceOne (Bulk) | ReadModify | 1000 | 81.4 ms | 1.00 | 7205 KB |
| Raw: Find + ReplaceOne (Manual Concurrency) | ReadModify | 1000 | 125.4 ms | 1.54 | 9604 KB |
| Raw: Find + UpdateOne.Set (Bulk) | ReadModify | 1000 | 95.1 ms | 1.17 | 11334 KB |
| Raw: Find + UpdateOne.Set (Manual Concurrency) | ReadModify | 1000 | 135.4 ms | 1.66 | 16404 KB |
| **Zen: Store() + SaveChanges (Concurrency ON)** | **Insert** | 1000 | **36.7 ms** | **1.11** | **1899 KB** |
| Raw: InsertManyAsync | Insert | 1000 | 33.0 ms | 1.00 | 1784 KB |
| **Zen: Attachments.Store + Get (1MB, Optimized)** | **Attachments** | 1000 | **57.1 ms** | **0.95** | **2331 KB** |
| Raw: GridFSBucket Upload + Download (1MB) | Attachments | 1000 | 60.1 ms | 1.00 | 6791 KB |

### What these numbers mean:

1.  **Identity Map (Repeated Reads)**: This is where we shine. Serving data from memory is ~65x faster than hitting the wire. If you already have the data, why ask for it again?
2.  **Read-Modify-Save (The "Smart Batching" Win)**: 
    *   Compare `Zen (Concurrency ON)` (73.8ms) vs `Raw (Manual Concurrency)` (135.4ms). Zen is **nearly 2x faster** than manually implementing a concurrency-safe bulk update. We handle the ceremony of diffing and batching more efficiently than you would by hand.
    *   Even without concurrency checks, Zen matches or beats raw bulk operations by using a more efficient update pipeline.
3.  **Insert (The Complexity Tax)**: We see a ~11% overhead on simple inserts. That's the price for setting up tracking and concurrency metadata. For a single-shot insert, raw is faster, but the moment you need a Unit of Work, Zen makes that time back on the next operation.
4.  **GridFS (Memory Efficiency)**: Zen uses **~65% less memory** than the raw GridFS bucket. By implementing intelligent chunk batching and pooling our internal `ArenaAllocator`, we've optimized for throughput while keeping the GC happy.
5.  **Allocations**: We focus on "lean" over "zero." By using single-pass diffing and direct BSON builders, we ensure that the overhead of change tracking doesn't turn into a GC nightmare.

## More Info

Check out our [Wiki](https://github.com/myarichuk/MongoZen/wiki) for:
*   [Detailed How-Tos](https://github.com/myarichuk/MongoZen/wiki/Getting-Started)
*   [Testing Strategies](https://github.com/myarichuk/MongoZen/wiki/Testing-With-InMemoryDbSet)
*   [Under the Hood: LINQ Translation](https://github.com/myarichuk/MongoZen/wiki/Under-The-Hood-LINQ-And-Include)

## License

MIT. Go build something cool.
