# MongoZen

MongoZen is a small, experimental MongoDB helper library with DbContext/DbSet-style abstractions.
**WIP: do not use in production.** The API and behavior are still changing.

## What it does
- DbContext/DbSet abstractions with LINQ-based queries
- In-memory DbSet for tests
- Filter-to-LINQ translation helpers
- Source generators for session wrappers
- Logical operator semantics aligned with MongoDB (e.g., empty $or/$and/$nor)

## Install
```bash
dotnet add package MongoZen
```

## Quick start
```csharp
public class MyDbContext : DbContext
{
    public IDbSet<MyEntity> MyEntities { get; set; }

    public MyDbContext(DbContextOptions options) : base(options) { }
}
```
