using System.Linq.Expressions;

namespace Library;

public abstract class DbContext
{
    protected DbContextOptions Options { get; }

    protected DbContext(DbContextOptions options)
    {
        Options = options;
        OnModelCreating();
    }

    protected virtual void OnModelCreating() { }

    protected IDbSet<T> InitSet<T>(string collectionName)
    {
        if (Options.UseInMemory)
            return new InMemoryDbSet<T>();

        if (Options.Mongo == null)
            throw new InvalidOperationException("Mongo database not configured.");

        var collection = Options.Mongo.GetCollection<T>(collectionName);
        return new DbSet<T>(collection);
    }
}