using System.Linq.Expressions;
using MongoDB.Driver;
// ReSharper disable ComplexConditionExpression
// ReSharper disable VirtualMemberCallInConstructor

namespace MongoZen;

public abstract class DbContext: IDisposable
{
    public DbContextOptions Options { get; }

    public void Dispose() => Options.Mongo?.Client.Dispose();

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
            .Where(p => p.PropertyType.IsGenericType && p.PropertyType.GetGenericTypeDefinition() == dbSetInterface)
            .ToArray();

        // sanity check
        if (!props.Any())
        {
            throw new InvalidOperationException("No IDbSet<T> properties defined. This is probably a bug.");
        }

        foreach (var prop in props)
        {
            if (!prop.CanWrite || !prop.CanRead)
            {
                throw new InvalidOperationException(
                    $"Property name {prop.Name} has no setter, but it should. Every IDbSet<T> property representing a collection must have a getter and a setter.");
            }

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
                    throw new InvalidOperationException("Mongo database not configured. This is not supposed to happen and is likely a bug.");
                }

                var getCollectionMethod =
                    typeof(IMongoDatabase).GetMethod(
                        nameof(IMongoDatabase.GetCollection),
                        [typeof(string), typeof(MongoCollectionSettings)]);
                var genericGetCollection = getCollectionMethod!.MakeGenericMethod(entityType);

                var collection = genericGetCollection.Invoke(Options.Mongo, [prop.Name, null])!;

                var constructed = typeof(DbSet<>).MakeGenericType(entityType);
                instance = Activator.CreateInstance(constructed, collection)!;
            }

            prop.SetValue(this, instance);
        }
    }
}