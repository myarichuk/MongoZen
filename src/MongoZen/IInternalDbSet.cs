using MongoDB.Driver;

namespace MongoZen;

internal interface IInternalDbSet<T> where T : class
{
    ValueTask CommitAsync(
        IEnumerable<T> added, 
        IEnumerable<T> removed, 
        IEnumerable<object> removedIds, 
        IEnumerable<T> updated, 
        IClientSessionHandle? session, 
        CancellationToken cancellationToken = default);
}
