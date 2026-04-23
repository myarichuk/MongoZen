# MongoZen
MongoZen is a lightweight, high-performance library that provides an **Entity Framework Core-like and RavenDB-like experience** for MongoDB. It bridges the gap between the flexibility of MongoDB and the structure of an ORM, providing a clean Unit of Work abstraction while maximizing database performance.

## Key Features
- **Unit of Work & Bulk Commits**:
    - Aggregates all additions, updates, and removals into a **single bulk operation** per collection, minimizing network round-trips.
    - **Identity Map & Change Tracking**: Automatically tracks fetched entities. Modifications are detected and saved without explicit `Update()` calls.
    - **Atomic by Default**: Operations are atomic when a transaction is active (default for Replica Sets/Sharded Clusters).
- **RavenDB-style API**:
    - Familiar `Store`, `Delete`, `LoadAsync`, and `Query<T>` methods on the session for a "no-nonsense" developer experience.
    - Automatic ID generation and tracking.
- **Source-Generated Efficiency**:
    - Uses Roslyn Source Generators to wire up `DbSet` properties and sessions at compile-time, **eliminating reflection** from the hot path.
- **In-Memory Database for Testing**:
    - Seamlessly switch between MongoDB and a fast, reliable in-memory provider for unit testing.
- **LINQ Support**:
    - Full LINQ to MongoDB integration for server-side filtering and querying.

## Installation
To get started, install the NuGet package:
``` bash
dotnet add package MongoZen
```

## Getting Started

### 1. Define Your `DbContext`
Create a **partial** class that inherits from `MongoZen.DbContext`. The `partial` keyword is required for source generators to provide optimized initialization logic.
``` csharp
public partial class MyDbContext : MongoZen.DbContext
{
    public IDbSet<Person> People { get; set; } = null!;

    public MyDbContext(DbContextOptions options) : base(options) { }
}
```

### 2. Configure `DbContextOptions`
Configure connection settings using `DbContextOptions.CreateForMongo`:
``` csharp
var options = DbContextOptions.CreateForMongo(
    "mongodb://localhost:27017", 
    "MyDatabase"
);

var context = new MyDbContext(options);
```

### 3. Usage (RavenDB Style)
Entities fetched within a session are tracked. Requesting the same entity by ID multiple times returns the **same object instance**, and any changes made to these objects are automatically detected during `SaveChangesAsync()`.

``` csharp
await using var session = context.StartSession();

// Fetch an entity
var person = await session.LoadAsync<Person>("alice-id");

// Modify properties directly - no Update() call required!
person.Age = 31;
person.Name = "Alice Smith";

// Querying
var results = await session.Query<Person>()
    .Where(p => p.Age > 30)
    .ToListAsync();

// All changes (including additions and removals) are flushed in a single bulk operation
await session.SaveChangesAsync();
```

### 4. Unit of Work Pattern
The `DbContextSession` acts as your Unit of Work. It tracks all changes locally and commits them in one go, optimizing database communication.

``` csharp
await using var session = context.StartSession();

// Store new entities
session.Store(new Person { Name = "Bob", Age = 30 });

// Delete entities
session.Delete(oldPerson);
session.Delete<Person>("some-id");

// Persist all changes atomically
await session.SaveChangesAsync();
```

### 5. Advanced Options
Internal tracking and management methods are tucked away under the `Advanced` property:
``` csharp
var dirty = session.People.Advanced.GetUpdated();
await session.Advanced.ClearTracking();
```

### 6. In-Memory Testing
Switch to the in-memory mode for fast, isolated unit testing:
``` csharp
var options = new DbContextOptions(); // Defaults to UseInMemory = true
var testContext = new MyDbContext(options);
```

> **Note on Cluster Topology:** MongoZen automatically detects if your cluster supports transactions (Replica Sets or Sharded Clusters) and caches this globally to prevent redundant server round-trips. For standalone instances, configure `TransactionSupportBehavior.Simulate` in your conventions to fall back to non-transactional commits.
## Documentation
Further documentation and details can be found on the [project's GitHub page](https://github.com/your-repo-url).
## Contributing
Contributions are welcome! Follow these steps to contribute:
1. Fork the repository.
2. Create a branch for your feature or bug fix.
3. Push your changes and make a pull request.

Before submitting PRs, ensure:
- Tests are added or updated.
- Code passes the precommit hooks (`stylecop.json` rules).
- Commit messages follow [Conventional Commits](https://www.conventionalcommits.org/).

## License
MongoZen is licensed under the MIT License. See the [LICENSE](LICENSE) file for details.
