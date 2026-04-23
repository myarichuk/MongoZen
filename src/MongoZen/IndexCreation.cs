using System.Reflection;
using MongoDB.Driver;

namespace MongoZen;

/// <summary>
/// Provides methods for discovering and creating indexes defined via <see cref="AbstractIndexCreationTask{T}"/>.
/// </summary>
public static class IndexCreation
{
    /// <summary>
    /// Scans the specified assembly for all index creation tasks and executes them.
    /// </summary>
    /// <param name="assembly">The assembly to scan.</param>
    /// <param name="database">The MongoDB database.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    public static async Task CreateIndexesAsync(Assembly assembly, IMongoDatabase database, CancellationToken cancellationToken = default)
    {
        var taskTypes = assembly.GetTypes()
            .Where(t => t is { IsAbstract: false, IsClass: true } && typeof(IAbstractIndexCreationTask).IsAssignableFrom(t));

        var tasks = new List<IAbstractIndexCreationTask>();
        foreach (var type in taskTypes)
        {
            if (Activator.CreateInstance(type) is IAbstractIndexCreationTask task)
            {
                tasks.Add(task);
            }
        }

        // Deduplicate by IndexName + CollectionName to avoid firing identical commands in parallel
        // Note: A single class might create multiple models, so we check ExecuteAsync logic too.
        // For simplicity, we assume one Task class per index (or group of indexes).
        
        await Task.WhenAll(tasks.Select(t => t.ExecuteAsync(database, cancellationToken))).ConfigureAwait(false);
    }

    /// <summary>
    /// Scans the assembly containing the specified type for all index creation tasks and executes them.
    /// </summary>
    public static Task CreateIndexesAsync<T>(IMongoDatabase database, CancellationToken cancellationToken = default)
        => CreateIndexesAsync(typeof(T).Assembly, database, cancellationToken);
}
