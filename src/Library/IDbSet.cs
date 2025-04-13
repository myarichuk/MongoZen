using MongoDB.Driver;

namespace Library;

public interface IDbSet<T> : IQueryable<T>
{
    ValueTask<IEnumerable<T>> QueryAsync(FilterDefinition<T> filter);
    ValueTask<IEnumerable<T>> QueryAsync(Expression<Func<T, bool>> filter);
}