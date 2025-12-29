using MongoDB.Driver;

namespace MongoZen;

/// <summary>
/// Represents a queryable set of entities with MongoDB filter helpers.
/// </summary>
/// <typeparam name="T">The entity type.</typeparam>
public interface IDbSet<T> : IQueryable<T>
{
    /// <summary>
    /// Executes a MongoDB filter against the collection.
    /// </summary>
    /// <param name="filter">The filter definition to apply.</param>
    /// <returns>A task that resolves to the matching entities.</returns>
    ValueTask<IEnumerable<T>> QueryAsync(FilterDefinition<T> filter);

    /// <summary>
    /// Executes a LINQ expression filter against the collection.
    /// </summary>
    /// <param name="filter">The expression to apply.</param>
    /// <returns>A task that resolves to the matching entities.</returns>
    ValueTask<IEnumerable<T>> QueryAsync(Expression<Func<T, bool>> filter);
}
