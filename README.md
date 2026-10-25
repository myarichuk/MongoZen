# MongoZen

MongoDB is nice and all, but the driver experience in C# usually sucks. You either end up with reflection-heavy "automagical" repositories, or you're writing manual BsonDocument boilerplate for aggregation pipelines like it's 2010.

**MongoZen** gives you the **RavenDB Experience** on top of MongoDB:
1.  **Zero-Allocation Shadow Tracking**: No cloning object graphs. We use raw BSON bytes in an Arena as the baseline.
2.  **True Unit of Work**: A scoped session with an Identity Map and automatic change detection.
3.  **Blittable Performance**: A custom BSON engine that is up to **171x faster** at parsing than the official driver.
4.  **RavenDB-style API**: `LoadAsync`, `Store`, `SaveChangesAsync`, and a first-class `Attachments` API.

## Quick Start

### 1. Initialize the Store
```csharp
var store = new DocumentStore("mongodb://localhost:27017", "MyDatabase");
```

### 2. Basic Session Usage
```csharp
using var session = store.OpenSession();

// Load a document (snapshot is captured in Arena memory)
var user = await session.LoadAsync<User>("users/1");

// Mutate the POCO
user.Name = "Oren Eini";
user.LastLogin = DateTime.UtcNow;

// Persist changes (only modified fields are sent via $set)
await session.SaveChangesAsync();
```

### 3. Attachments API
```csharp
// Store a large blob linked to a document
using var stream = File.OpenRead("profile.jpg");
await session.Attachments.StoreAsync("users/1", "avatar.jpg", stream, "image/jpeg");

// Retrieve it
using var attachment = await session.Attachments.GetAsync("users/1", "avatar.jpg");
Process(attachment.Stream);

// Attachments are automatically scrubbed if the document is deleted!
session.Delete(user);
await session.SaveChangesAsync(); // Deletes document AND its GridFS files
```
Attachment writes (`StoreAsync`, `DeleteAsync`, `DeleteAllAsync`) are part of the session's unit of
work: on a replica set or sharded cluster they run inside the transaction that the next
`SaveChangesAsync()` commits, and disposing the session without saving rolls them back. Call
`SaveChangesAsync()` after attachment writes even if no entities changed. Reads (`GetAsync`,
`GetNamesAsync`) never open a transaction. On a standalone server, where MongoDB has no
transactions, attachment writes execute immediately.

### 4. Hi/Lo ID Generation
```csharp
public class User
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
}

var user = new User { Name = "Oren Eini" };
session.Store(user); // no Id set: MongoZen assigns "users/1" (RavenDB-style) synchronously
await session.SaveChangesAsync();
```
Ids are claimed from an in-memory `[low, high)` range per collection tag, backed by a counter
document in a `HiLo` collection (configurable via `DocumentConventions.HiLoCollectionName`/
`HiLoCapacity`). Only a cold refill (range exhausted) makes a blocking DB round-trip. This only
applies to entities with a **settable `string` Id property** — other Id types (`Guid`,
`ObjectId`, `int`, ...) still require you to set the Id yourself before `Store()`. Mutating an
entity's Id after `Store()` throws `InvalidOperationException`.

### 5. Querying Beyond Load/Query
```csharp
long total = await session.CountAsync<User>(u => u.Age >= 18);
bool any = await session.AnyAsync<User>(u => u.Name == "Oren Eini");

// Untracked, non-buffered streaming — mutations on yielded entities are NOT persisted.
await foreach (var user in session.StreamAsync(Builders<User>.Filter.Empty))
{
    Process(user);
}
```

### 6. Bulk Filter-Based Mutations
```csharp
// Executes immediately against the server — NOT deferred to SaveChangesAsync, and it
// bypasses the identity map, so already-loaded entities can go stale relative to the DB.
await session.Advanced.UpdateManyAsync<User>(
    Builders<User>.Filter.Eq(u => u.Active, false),
    Builders<User>.Update.Set(u => u.Archived, true));

await session.Advanced.DeleteManyAsync<User>(Builders<User>.Filter.Eq(u => u.Active, false));
```

### 7. Transactions
Each `SaveChangesAsync()` is one transaction when the deployment supports them (replica set or
sharded cluster): it is started by the save (or earlier, by an attachment write), committed before
the save returns, and aborted if anything throws. A session can call `SaveChangesAsync()` any number
of times and each call gets its own transaction. Loads, queries and attachment reads do not open a
transaction; `session.Advanced.IsTransactional` reports whether one is currently open.

On a standalone server there are no transactions, so a `SaveChangesAsync()` touching more than one
distinct `(collection, operation type)` group is non-atomic. Set `RequireTransactions` to fail
loudly instead of silently degrading:
```csharp
var store = new DocumentStore(connectionString, "MyDatabase",
    new DocumentConventions { RequireTransactions = true });

// Throws TransactionRequirementException if the server can't provide a transaction
// for a multi-group save, instead of silently falling back to non-atomic writes.
```

## Supported Property Types

Both tiers share one type model. A property, a collection element and a dictionary value of the same
type are always serialized identically.

| .NET type | BSON |
| :--- | :--- |
| `int`, `short`, `ushort`, `byte`, `sbyte`, `char` | Int32 |
| `long`, `uint`, `ulong` | Int64 |
| `double`, `float` | Double |
| `bool`, `string`, `ObjectId`, `DateTime` (ms precision, UTC), `Guid` (subtype 4), `decimal` (Decimal128) | the matching BSON type |
| enums | underlying Int32/Int64 |
| `Nullable<T>` of the above | the value, or Null |
| `byte[]` | Binary (subtype 0) |
| arrays and anything implementing `IEnumerable<T>` | Array (read back into arrays, `List<T>`, interfaces, or any type with an `IEnumerable<T>` constructor) |
| `IDictionary<string, V>` / `IReadOnlyDictionary<string, V>` | Document keyed by the dictionary keys |
| your own classes and structs | nested Document (classes carrying driver attributes such as `[BsonRepresentation]` go through the official driver's serializer) |

Null references and empty `Nullable<T>`s are written as BSON null, as the official driver does.
Get-only (computed) properties are not serialized. Anything else (`TimeSpan`, `DateTimeOffset`,
`DateOnly`, `object`, dictionaries with non-string keys, ...) throws a `NotSupportedException`
naming the offending member on first use of the entity type, in the reflection tier, or an `MZ001`
compile error from the source generator. Register an `IBlittableConverter<T>` through
`BlittableConverter<T>.Register` before first use to add a type yourself.

## Identity Map

A session tracks exactly one instance per `(collection, id)`. `LoadAsync` returns the tracked
instance when there is one; numeric ids are normalized to the entity's id type, so loading a `long`
id with an `int` finds the same instance. `Store` of the instance already tracked is a no-op; `Store`
of a *different* instance with a tracked id throws (`Advanced.Evict` the first one if you really
mean to replace it). Deleting an entity removes it from the map once `SaveChangesAsync` runs.

## Architectural Tiers

*   **Tier 1 (Source Gen)**: Compile-time non-allocating diffing.
*   **Tier 2 (Dynamic Reflection)**: Compiled Expression Trees for zero-allocation runtime serialization.
*   **Tier 3 (Driver Bridge)**: 100% compatibility fallback for complex custom driver configurations.

Collections and dictionaries are recognized structurally (anything implementing `IEnumerable<T>` or
`IDictionary<string,V>`/`IReadOnlyDictionary<string,V>`), so third-party collection types such as
Google.Protobuf's `RepeatedField<T>`/`MapField<K,V>` are written correctly. Reading back needs the
property type to be an array, an interface, or a type with a public `IEnumerable<T>` (or
`IDictionary<string,V>`) constructor; otherwise the serializer for that entity throws a
`NotSupportedException` saying so.

## Performance vs. Official Driver

| Operation | Gain |
| :--- | :--- |
| **BSON Parsing/Indexing** | **~171x Faster** (Zero GC) |
| **Serialization** | **~1.9x Faster** (~100% less GC) |
| **Identity Map** | **O(1)** lookup, Zero allocation |

## License

MIT. Go build something fast.
