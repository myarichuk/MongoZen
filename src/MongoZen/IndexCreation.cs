using System.Reflection;

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
    /// <param name="store">The DocumentStore instance.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    public static async Task CreateIndexesAsync(Assembly assembly, DocumentStore store, CancellationToken cancellationToken = default)
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

        await Task.WhenAll(tasks.Select(t => t.ExecuteAsync(store, cancellationToken))).ConfigureAwait(false);
    }

    /// <summary>
    /// Scans the assembly containing the specified type for all index creation tasks and executes them.
    /// </summary>
    public static Task CreateIndexesAsync<T>(DocumentStore store, CancellationToken cancellationToken = default)
        => CreateIndexesAsync(typeof(T).Assembly, store, cancellationToken);
}
