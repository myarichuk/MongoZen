using MongoDB.Driver;
using MongoZen.Collections;

namespace MongoZen;

internal interface IInternalDbSet<T> where T : class
{
    ValueTask CommitAsync(
        IEnumerable<T> added, 
        IEnumerable<T> removed, 
        IEnumerable<object> removedIds, 
        IEnumerable<T> updated, 
        IEnumerable<T> dirty, 
        PooledDictionary<DocId, (T Entity, bool IsDirty)> upsertBuffer,
        PooledHashSet<object> rawIdBuffer,
        PooledList<MongoDB.Driver.WriteModel<T>> modelBuffer,
        Func<T, IntPtr, UpdateDefinition<T>?>? extractor,
        ISessionTracker tracker,
        TransactionContext transaction, 
        SharpArena.Allocators.ArenaAllocator arena,
        CancellationToken cancellationToken = default);
}
