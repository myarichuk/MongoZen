# MongoZen
is a lightweight, developer-friendly library that provides an **Entity Framework Core-like experience** for MongoDB. It bridges the gap between the flexibility of MongoDB and the structure of an ORM, making it easier to query, manage, and interact with your MongoDB collections using object-oriented patterns.
## Features
- **EF-Core-Like Abstractions**:
    - for centralizing database access logic and managing collections (). `DbContext``IDbSet`
    - LINQ support, including server-side filtering and querying.

- **In-Memory Database for Testing**:
    - Seamlessly switch between MongoDB collections and in-memory collections for unit testing.

- **BSON and MongoDB Integration**:
    - Supports MongoDB specifics like for custom ID handling. `[BsonId]`
    - Handles default and custom ID conventions with flexibility.

- **Source Generators**:
    - Simplifies repetitive tasks such as entity mapping.

- **Comprehensive Testing**:
    - Robust test coverage with integration and unit tests.

- **CI/CD and Precommit Hook Integration**:
    - StyleCop for consistent code formatting.
    - GitHub Actions for automated builds and releases.

## Installation
To get started, install the NuGet package:
``` bash
dotnet add package MongoFlow
```
## Getting Started
### 1. Define Your `DbContext`
Create a class that inherits from `MongoFlow.DbContext`:
> Note that ``IDbSet<TEntity>`` properties with public get and set are required
``` csharp
public class MyDbContext : DbContext
{
    public IDbSet<MyEntity> MyEntities { get; set; }

    public MyDbContext(DbContextOptions options) : base(options) { }
}
```
### 2. Define Your DAL Entities

Define DAL-specific entities with required ID properties and MongoDB attributes:
``` csharp
[BsonIgnoreExtraElements]
public class MyEntity
{
    [BsonId]
    public string Id { get; set; }
    public string Name { get; set; }
    public int Age { get; set; }
}
```
### 3. Configure `DbContextOptions`
Configure connection settings using : `DbContextOptions`
``` csharp
var options = new DbContextOptions
{
    Mongo = new MongoDbOptions
    {
        ConnectionString = "mongodb://localhost:27017",
        DatabaseName = "MyDatabase"
    }
};

var context = new MyDbContext(options);
```
### 4. Query with LINQ
Use LINQ or MongoDB filters to query data:
``` csharp
var results = await context.MyEntities.QueryAsync(e => e.Age > 30);
```
### 5. In-Memory Testing
Switch to the in-memory mode for safe unit testing:
``` csharp
var inMemoryOptions = new DbContextOptions(); // In-memory mode enabled
var testContext = new MyDbContext(inMemoryOptions);
```
## Documentation
Further documentation and details can be found on the [project's GitHub page](https://github.com/your-repo-url).
## Contributing
Contributions are welcome! Follow these steps to contribute:
1. Fork the repository.
2. Create a branch for your feature or bug fix.
3. Push your changes and make a pull request.

Before submitting PRs, ensure:
- Tests are added or updated.
- Code passes the precommit hooks ( rules). `stylecop.json`
- Commit messages follow [Conventional Commits](https://www.conventionalcommits.org/).

## License
MongoFlow is licensed under the MIT License. See the [LICENSE](LICENSE) file for details.
