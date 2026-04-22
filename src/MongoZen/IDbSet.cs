using System.Linq.Expressions;
using MongoDB.Driver;

namespace MongoZen;

public interface IDbSet<T> : IQueryable<T> where T : class
{
    string CollectionName { get; }

    ValueTask<IEnumerable<T>> QueryAsync(FilterDefinition<T> filter, CancellationToken cancellationToken = default);

    ValueTask<IEnumerable<T>> QueryAsync(Expression<Func<T, bool>> filter, CancellationToken cancellationToken = default);

    ValueTask<IEnumerable<T>> QueryAsync(FilterDefinition<T> filter, IClientSessionHandle session, CancellationToken cancellationToken = default);

    ValueTask<IEnumerable<T>> QueryAsync(Expression<Func<T, bool>> filter, IClientSessionHandle session, CancellationToken cancellationToken = default);
}
