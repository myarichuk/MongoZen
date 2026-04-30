using System.ComponentModel;
using MongoDB.Driver;

// ReSharper disable ComplexConditionExpression
// ReSharper disable VirtualMemberCallInConstructor

namespace MongoZen;

public abstract partial class DbContext : IDisposable
{
    public DbContextOptions Options { get; }

    internal string GridFSBucketName { get; } = "fs";

    internal System.Collections.Concurrent.ConcurrentDictionary<string, InMemoryFileData>? InMemoryAttachments { get; }

    protected DbContext(DbContextOptions options)
    {
        Options = options;

        if (options.UseInMemory)
        {
            InMemoryAttachments = new System.Collections.Concurrent.ConcurrentDictionary<string, InMemoryFileData>();
        }
        else if (options.Mongo != null)
        {
            GridFSBucketName = options.GridFSOptions?.BucketName ?? "fs";
        }

        InitializeDbSets();
        OnModelCreating();
    }

    public void Dispose()
    {
        if (Options.OwnsClient)
        {
            Options.Mongo?.Client.Dispose();
        }
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Returns the MongoDB collection name for the given entity type.
    /// Source generators will override this method to provide a high-performance, reflection-free implementation.
    /// </summary>
    public abstract string GetCollectionName(Type entityType);

    protected virtual void OnModelCreating()
    {
    }

    /// <summary>
    /// Creates all indexes defined in the assemblies configured in <see cref="DbContextOptions.IndexDiscoveryAssemblies"/>.
    /// If no assemblies are configured, scans the assembly containing this DbContext.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    public async Task CreateIndexesAsync(CancellationToken cancellationToken = default)
    {
        if (Options.Mongo == null || Options.UseInMemory)
        {
            return;
        }

        var assemblies = Options.IndexDiscoveryAssemblies;
        if (assemblies == null || assemblies.Count == 0)
        {
            assemblies = new List<System.Reflection.Assembly> { GetType().Assembly };
        }

        foreach (var assembly in assemblies)
        {
            await IndexCreation.CreateIndexesAsync(assembly, Options.Mongo, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Initializes DbSet properties.
    /// Source generators will override this method to provide a high-performance, reflection-free implementation.
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    protected abstract void InitializeDbSets();

}
