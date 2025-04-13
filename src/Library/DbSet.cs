using System.Collections;
using MongoDB.Driver;

namespace Library;

public class DbSet<T> : IDbSet<T>
{
    private readonly IQueryable<T> _collectionAsQueryable;
    private readonly IMongoCollection<T> _collection;

    public DbSet(IMongoCollection<T> collection)
    {
        _collection = collection;
        _collectionAsQueryable = collection.AsQueryable();
    }

    public async ValueTask<IEnumerable<T>> QueryAsync(FilterDefinition<T> filter) => 
        await (await _collection.FindAsync(filter)).ToListAsync();

    public async ValueTask<IEnumerable<T>> QueryAsync(Expression<Func<T, bool>> filter) =>
        await (await _collection.FindAsync(Builders<T>.Filter.Where(filter))).ToListAsync();

    public IEnumerator<T> GetEnumerator() => _collectionAsQueryable.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public Type ElementType => _collectionAsQueryable.ElementType;
    public Expression Expression => _collectionAsQueryable.Expression;
    public IQueryProvider Provider => _collectionAsQueryable.Provider;
}