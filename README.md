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

## Architectural Tiers

*   **Tier 1 (Source Gen)**: Compile-time non-allocating diffing.
*   **Tier 2 (Dynamic Reflection)**: Compiled Expression Trees for zero-allocation runtime serialization.
*   **Tier 3 (Driver Bridge)**: 100% compatibility fallback for complex custom driver configurations.

## Performance vs. Official Driver

| Operation | Gain |
| :--- | :--- |
| **BSON Parsing/Indexing** | **~171x Faster** (Zero GC) |
| **Serialization** | **~1.9x Faster** (~100% less GC) |
| **Identity Map** | **O(1)** lookup, Zero allocation |

## License

MIT. Go build something fast.
