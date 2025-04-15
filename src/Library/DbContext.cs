using System.Linq.Expressions;
using MongoDB.Driver;

namespace Library;

public abstract class DbContext: IDisposable
{
    protected DbContextOptions Options { get; }

    protected DbContext(DbContextOptions options)
    {
        Options = options;
        InitializeDbSets();
        OnModelCreating();
    }

    protected virtual void OnModelCreating() { }

    /// <summary>
    /// Override this partial method in a partial class to initialize DbSet properties.
    /// This mimics EFCore-style property wiring without reflection or code generation.
    /// </summary>
    protected virtual void InitializeDbSets()
    {
        var dbSetInterface = typeof(IDbSet<>);
        var props = GetType()
            .GetProperties(System.Reflection.BindingFlags.Public |
                           System.Reflection.BindingFlags.NonPublic |
                           System.Reflection.BindingFlags.Instance)
            .Where(p => p.PropertyType.IsGenericType && p.PropertyType.GetGenericTypeDefinition() == dbSetInterface);

        foreach (var prop in props)
        {
            var entityType = prop.PropertyType.GetGenericArguments()[0];
            object instance;

            if (Options.UseInMemory)
            {
                var constructed = typeof(InMemoryDbSet<>).MakeGenericType(entityType);
                instance = Activator.CreateInstance(constructed)!;
            }
            else
            {
                if (Options.Mongo == null)
                {
                    throw new InvalidOperationException("Mongo database not configured");
                }

                var getCollectionMethod = typeof(IMongoDatabase).GetMethod(nameof(IMongoDatabase.GetCollection),
                                              [typeof(string), typeof(MongoDB.Bson.Serialization.IBsonSerializer)]) ??
                                          typeof(IMongoDatabase).GetMethod(nameof(IMongoDatabase.GetCollection),
                                              [typeof(string)]);
                var collection = getCollectionMethod!.MakeGenericMethod(entityType)
                    .Invoke(Options.Mongo, [prop.Name])!;

                var constructed = typeof(DbSet<>).MakeGenericType(entityType);
                instance = Activator.CreateInstance(constructed, collection)!;
            }

            prop.SetValue(this, instance);
        }
    }

    protected IDbSet<T> InitSet<T>(string collectionName)
    {
        if (Options.UseInMemory)
        {
            return new InMemoryDbSet<T>();
        }

        if (Options.Mongo == null)
        {
            throw new InvalidOperationException("Mongo database not configured.");
        }

        var collection = Options.Mongo.GetCollection<T>(collectionName);
        return new DbSet<T>(collection);
    }

    public void Dispose() => Options.Mongo?.Client.Dispose();
}