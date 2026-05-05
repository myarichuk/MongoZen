using System;
using System.Threading;
using System.Threading.Tasks;

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
    Task RefreshAsync<T>(T entity, CancellationToken ct = default);
}
