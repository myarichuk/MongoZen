using System;
using System.Threading;
using System.Threading.Tasks;
using MongoDB.Driver;

namespace MongoZen;

/// <summary>
/// Advanced operations for the <see cref="DocumentSession"/>.
/// </summary>
public interface ISessionAdvancedOperations
{
    /// <summary>
    /// Gets the expected ETag for the given entity.
    /// </summary>
    Guid? GetETagFor(object entity);

    /// <summary>
    /// Stores an entity with a specific expected ETag for concurrency control.
    /// </summary>
    void Store(object entity, Guid expectedEtag);

    /// <summary>
    /// Evicts the entity from the session. It will no longer be tracked and will be removed from the identity map.
    /// </summary>
    void Evict(object entity);

    /// <summary>
    /// Refreshes the entity from the database, updating its properties and resetting its change tracking state.
    /// </summary>
    ValueTask RefreshAsync<T>(T entity, CancellationToken ct = default);

    /// <summary>
    /// Gets a value indicating whether a transaction is currently active for this session.
    /// </summary>
    bool IsTransactional { get; }

    /// <summary>
    /// Updates many documents matching the filter without loading entities.
    /// This operation executes immediately and is not deferred to SaveChangesAsync.
    /// Previously-loaded entities may become stale relative to the database.
    /// </summary>
    ValueTask<long> UpdateManyAsync<T>(FilterDefinition<T> filter, UpdateDefinition<T> update, CancellationToken ct = default);

    /// <summary>
    /// Deletes many documents matching the filter without loading entities.
    /// This operation executes immediately and is not deferred to SaveChangesAsync.
    /// Previously-loaded entities may become stale relative to the database.
    /// </summary>
    ValueTask<long> DeleteManyAsync<T>(FilterDefinition<T> filter, CancellationToken ct = default);
}
