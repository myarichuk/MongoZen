namespace MongoZen;

/// <summary>
/// Represents a mutable set of entities that tracks changes before committing.
/// </summary>
/// <typeparam name="T">The entity type.</typeparam>
public interface IMutableDbSet<T> : IDbSet<T>
{
    /// <summary>
    /// Adds an entity to the pending insert list.
    /// </summary>
    /// <param name="entity">The entity to add.</param>
    void Add(T entity);

    /// <summary>
    /// Adds an entity to the pending delete list.
    /// </summary>
    /// <param name="entity">The entity to remove.</param>
    void Remove(T entity);

    /// <summary>
    /// Adds an entity to the pending update list.
    /// </summary>
    /// <param name="entity">The entity to update.</param>
    void Update(T entity);

    /// <summary>
    /// Returns the entities staged for insertion.
    /// </summary>
    IEnumerable<T> GetAdded();

    /// <summary>
    /// Returns the entities staged for removal.
    /// </summary>
    IEnumerable<T> GetRemoved();

    /// <summary>
    /// Returns the entities staged for update.
    /// </summary>
    IEnumerable<T> GetUpdated();

    /// <summary>
    /// Persists staged changes to the underlying store.
    /// </summary>
    Task CommitAsync(); // eventually used in SaveChanges()
}
