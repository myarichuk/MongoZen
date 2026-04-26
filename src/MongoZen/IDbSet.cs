using System.Linq.Expressions;
using MongoDB.Driver;

namespace MongoZen;

public interface IDbSet<T> where T : class
{
    string CollectionName { get; }

    ValueTask<IEnumerable<T>> QueryAsync(FilterDefinition<T> filter, CancellationToken cancellationToken = default);

    ValueTask<IEnumerable<T>> QueryAsync(Expression<Func<T, bool>> filter, CancellationToken cancellationToken = default);

    ValueTask<T?> LoadAsync(object id, CancellationToken cancellationToken = default);

    IDbSet<T> Include(Expression<Func<T, object?>> path);

    IDbSet<T> Include<TInclude>(Expression<Func<T, object?>> path) where TInclude : class;
}
