namespace MongoZen;

public interface IMutableDbSet<T> : IDbSet<T>
{
    void Add(T entity);

    void Remove(T entity);

    void Update(T entity);

    IEnumerable<T> GetAdded();

    IEnumerable<T> GetRemoved();

    IEnumerable<T> GetUpdated();

    Task CommitAsync(TransactionContext transaction); // eventually used in SaveChanges()
}
