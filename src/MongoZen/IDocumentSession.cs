using System.Linq.Expressions;
using MongoDB.Driver;

namespace MongoZen;

/// <summary>
/// Mockable contract for a MongoZen session. Provides core session operations without exposing internal MongoDB/blittable details.
/// </summary>
public interface IDocumentSession : IDisposable
{
    /// <summary>
    /// Gets attachment operations for this session.
    /// </summary>
    IAttachmentsSessionOperations Attachments { get; }

    /// <summary>
    /// Gets advanced session operations.
    /// </summary>
    ISessionAdvancedOperations Advanced { get; }

    /// <summary>
    /// Loads a document by ID.
    /// </summary>
    ValueTask<T?> LoadAsync<T>(object id, CancellationToken ct = default);

    /// <summary>
    /// Stores an entity in the session. If the entity has no ID and has a settable string ID property, a Hi/Lo ID is generated.
    /// </summary>
    void Store<T>(T entity);

    /// <summary>
    /// Stores an entity in the session, same as <see cref="Store{T}"/>, but awaits the Hi/Lo ID
    /// generator's refill instead of blocking the calling thread on it. Prefer this overload on
    /// hot write paths where a synchronous DB round-trip inside <see cref="Store{T}"/> (only
    /// triggered when the current Hi/Lo range is exhausted) would be undesirable.
    /// </summary>
    ValueTask StoreAsync<T>(T entity, CancellationToken ct = default);

    /// <summary>
    /// Marks an entity for deletion.
    /// </summary>
    void Delete<T>(T entity);

    /// <summary>
    /// Persists all changes to the database.
    /// </summary>
    Task SaveChangesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Queries documents with a filter definition.
    /// </summary>
    ValueTask<IReadOnlyList<T>> QueryAsync<T>(FilterDefinition<T> filter, SortDefinition<T>? sort = null, int? skip = null, int? limit = null, CancellationToken ct = default);

    /// <summary>
    /// Queries documents with a LINQ expression.
    /// </summary>
    ValueTask<IReadOnlyList<T>> QueryAsync<T>(Expression<Func<T, bool>> filter, SortDefinition<T>? sort = null, int? skip = null, int? limit = null, CancellationToken ct = default);

    /// <summary>
    /// Aggregates documents using a pipeline.
    /// </summary>
    ValueTask<IReadOnlyList<TResult>> AggregateAsync<T, TResult>(PipelineDefinition<T, TResult> pipeline, AggregateOptions? options = null, CancellationToken ct = default);

    /// <summary>
    /// Counts documents matching a filter.
    /// </summary>
    ValueTask<long> CountAsync<T>(FilterDefinition<T> filter, CancellationToken ct = default);

    /// <summary>
    /// Counts documents matching a LINQ expression.
    /// </summary>
    ValueTask<long> CountAsync<T>(Expression<Func<T, bool>> filter, CancellationToken ct = default);

    /// <summary>
    /// Checks if any documents match a filter.
    /// </summary>
    ValueTask<bool> AnyAsync<T>(FilterDefinition<T> filter, CancellationToken ct = default);

    /// <summary>
    /// Checks if any documents match a LINQ expression.
    /// </summary>
    ValueTask<bool> AnyAsync<T>(Expression<Func<T, bool>> filter, CancellationToken ct = default);

    /// <summary>
    /// Streams documents without tracking them. Results are not persisted on SaveChangesAsync.
    /// </summary>
    IAsyncEnumerable<T> StreamAsync<T>(FilterDefinition<T> filter, SortDefinition<T>? sort = null, int? skip = null, int? limit = null, CancellationToken ct = default);
}
