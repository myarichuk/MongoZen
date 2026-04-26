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
        Dictionary<DocId, T> upsertBuffer,
        HashSet<DocId> dedupeBuffer,
        HashSet<object> rawIdBuffer,
        List<MongoDB.Driver.WriteModel<T>> modelBuffer,
        TransactionContext transaction, 
        CancellationToken cancellationToken = default);
}
