using MongoDB.Driver;

namespace MongoZen;

internal interface IInternalDbSet<T> where T : class
{
    ValueTask CommitAsync(
        IEnumerable<T> added, 
        IEnumerable<T> removed, 
        IEnumerable<object> removedIds, 
        IEnumerable<T> updated, 
        IEnumerable<T> dirty, 
        Dictionary<object, T> upsertBuffer,
        HashSet<object> removedIdBuffer,
        List<MongoDB.Driver.WriteModel<T>> modelBuffer,
        IClientSessionHandle? session, 
        CancellationToken cancellationToken = default);
}
