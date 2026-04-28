namespace MongoZen;

public interface IMutableDbSet<T> : IDbSet<T> where T : class
{
    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
    void Add(T entity);

    /// <summary>
    /// Starts tracking the entity as "Unchanged".
    /// If the entity is later modified, it will be saved during SaveChangesAsync.
    /// </summary>
    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
    void Attach(T entity);

    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
    void Remove(T entity);

    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
    void Remove(object id);

    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
    void Remove(in DocId id);

    /// <summary>
    /// RavenDB-compatible alias for Add.
    /// </summary>
    void Store(T entity);

    /// <summary>
    /// RavenDB-compatible alias for Remove.
    /// </summary>
    void Delete(T entity);

    /// <summary>
    /// RavenDB-compatible alias for Remove.
    /// </summary>
    void Delete(object id);

    /// <summary>
    /// RavenDB-compatible alias for Remove.
    /// </summary>
    void Delete(in DocId id);

    new ValueTask<T?> LoadAsync(object id, CancellationToken cancellationToken = default);

    new IMutableDbSet<T> Include(System.Linq.Expressions.Expression<Func<T, object?>> path);

    new IMutableDbSet<T> Include<TInclude>(System.Linq.Expressions.Expression<Func<T, object?>> path) where TInclude : class;

    IMutableDbSetAdvanced<T> Advanced { get; }
}

public interface IMutableDbSetAdvanced<T> where T : class
{
    IEnumerable<T> GetAdded();

    IEnumerable<T> GetRemoved();

    IEnumerable<T> GetUpdated();

    /// <summary>
    /// Clears all tracked adds, removes, and updates.
    /// Called after a successful transaction commit to reset tracking state.
    /// </summary>
    void ClearTracking();
}
