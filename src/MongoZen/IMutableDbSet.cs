namespace MongoZen;

public interface IMutableDbSet<T> : IDbSet<T> where T : class
{
    void Add(T entity);

    /// <summary>
    /// Starts tracking the entity as "Unchanged".
    /// If the entity is later modified, it will be saved during SaveChangesAsync.
    /// </summary>
    void Attach(T entity);

    void Remove(T entity);

    void Remove(object id);

    new ValueTask<T?> LoadAsync(object id, CancellationToken cancellationToken = default);

    new IMutableDbSet<T> Include(System.Linq.Expressions.Expression<Func<T, object?>> path);

    IEnumerable<T> GetAdded();

    IEnumerable<T> GetRemoved();

    IEnumerable<T> GetUpdated();


    Task CommitAsync(TransactionContext transaction, CancellationToken cancellationToken = default); // eventually used in SaveChanges()

    /// <summary>
    /// Clears all tracked adds, removes, and updates.
    /// Called after a successful transaction commit to reset tracking state.
    /// </summary>
    void ClearTracking();
}
