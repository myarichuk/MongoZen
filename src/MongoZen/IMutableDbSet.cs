namespace MongoZen;

public interface IMutableDbSet<T> : IDbSet<T>
{
    void Add(T entity);

    void Remove(T entity);

    void Update(T entity);

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
