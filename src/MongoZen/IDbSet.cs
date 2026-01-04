using System.Linq.Expressions;
using MongoDB.Driver;

namespace MongoZen;

public interface IDbSet<T> : IQueryable<T>
{
    ValueTask<IEnumerable<T>> QueryAsync(FilterDefinition<T> filter);

    ValueTask<IEnumerable<T>> QueryAsync(Expression<Func<T, bool>> filter);

    ValueTask<IEnumerable<T>> QueryAsync(FilterDefinition<T> filter, IClientSessionHandle session);

    ValueTask<IEnumerable<T>> QueryAsync(Expression<Func<T, bool>> filter, IClientSessionHandle session);
}
