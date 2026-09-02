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

### 7. Transaction Requirement
A `SaveChangesAsync()` touching more than one distinct `(collection, operation type)` group is
non-atomic by default (today's opportunistic, silently-degrading transaction attempt). Set
`RequireTransactions` to fail loudly instead:
```csharp
var store = new DocumentStore(connectionString, "MyDatabase",
    new DocumentConventions { RequireTransactions = true });

// Throws TransactionRequirementException if the server can't provide a transaction
// for a multi-group save, instead of silently falling back to non-atomic writes.
```

## Architectural Tiers

*   **Tier 1 (Source Gen)**: Compile-time non-allocating diffing.
*   **Tier 2 (Dynamic Reflection)**: Compiled Expression Trees for zero-allocation runtime serialization.
*   **Tier 3 (Driver Bridge)**: 100% compatibility fallback for complex custom driver configurations.

Tier 2 recognizes collection/dictionary-shaped properties structurally — any type implementing
`IList<T>`/`IReadOnlyList<T>`/`ICollection<T>`/`IEnumerable<T>`, or `IDictionary<string,V>`/
`IReadOnlyDictionary<string,V>`, not just `List<T>`/`Dictionary<string,V>` themselves. This means
third-party collection types (e.g. Google.Protobuf's `RepeatedField<T>`/`MapField<K,V>`) are picked
up as collections/dictionaries on write rather than being serialized as an empty-looking nested
document. Reading back into one of these properties is still only guaranteed for arrays and
`List<T>`; other concrete collection types (again, `RepeatedField<T>` is an example) don't have a
matching constructor and will throw on deserialization rather than silently dropping data.

## Performance vs. Official Driver

| Operation | Gain |
| :--- | :--- |
| **BSON Parsing/Indexing** | **~171x Faster** (Zero GC) |
| **Serialization** | **~1.9x Faster** (~100% less GC) |
| **Identity Map** | **O(1)** lookup, Zero allocation |

## License

MIT. Go build something fast.
