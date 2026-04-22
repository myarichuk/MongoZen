using System.Collections;
using System.Text.Json;
using MongoDB.Driver;

// ReSharper disable ComplexConditionExpression
// ReSharper disable MethodTooLong

namespace MongoZen;

public class MutableDbSet<TEntity> : IMutableDbSet<TEntity>
{
    private readonly IDbSet<TEntity> _baseSet;
    private readonly Func<TransactionContext>? _transactionProvider;
    private readonly Func<TEntity, object?> _idAccessor;
    private readonly string _idFieldName;

    private readonly List<TEntity> _added = [];
    private readonly List<TEntity> _removed = [];
    private readonly List<TEntity> _updated = [];

    public MutableDbSet(IDbSet<TEntity> baseSet, Conventions? conventions = null)
    {
        _baseSet = baseSet;
        var properConventions = conventions ?? new();
        _idAccessor = EntityIdAccessor<TEntity>.GetAccessor(properConventions.IdConvention);
        _idFieldName = properConventions.IdConvention.ResolveIdProperty<TEntity>()?.Name ?? "_id";
    }

    public MutableDbSet(IDbSet<TEntity> baseSet, Func<TransactionContext> transactionProvider, Conventions? conventions = null)
        : this(baseSet, conventions)
    {
        _transactionProvider = transactionProvider;
    }

    public void Add(TEntity entity) => _added.Add(entity);

    public void Remove(TEntity entity) => _removed.Add(entity);

    public void Update(TEntity entity) => _updated.Add(entity);

    public IEnumerable<TEntity> GetAdded() => _added;

    public IEnumerable<TEntity> GetRemoved() => _removed;

    public IEnumerable<TEntity> GetUpdated() => _updated;

    public async Task CommitAsync(TransactionContext transaction, CancellationToken cancellationToken = default)
    {
        if (!transaction.IsActive)
        {
            throw new InvalidOperationException("A transaction is required to commit changes. Start a session with StartSession() and pass the transaction to CommitAsync().");
        }

        switch (_baseSet)
        {
            case InMemoryDbSet<TEntity> memSet:
                if (!transaction.IsInMemoryTransaction)
                {
                    throw new InvalidOperationException("In-memory commits require an in-memory transaction.");
                }

                await InternalCommitAsync(memSet, cancellationToken);
                break;
            case DbSet<TEntity> mongoSet:
                await InternalCommitAsync(mongoSet, transaction.Session, cancellationToken);
                break;
            default:
                throw new NotSupportedException($"The type {_baseSet.GetType()} is not supported.");
        }
    }

    /// <summary>
    /// Clears all tracked adds, removes, and updates.
    /// Called by the generated SaveChangesAsync after a successful transaction commit.
    /// </summary>
    public void ClearTracking()
    {
        _added.Clear();
        _removed.Clear();
        _updated.Clear();
    }

    // IQueryable passthrough
    public IEnumerator<TEntity> GetEnumerator() => throw new NotSupportedException("Use QueryAsync for asynchronous execution instead of synchronous LINQ evaluation.");

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public Type ElementType => _baseSet.ElementType;

    public Expression Expression => _baseSet.Expression;

    public IQueryProvider Provider => _baseSet.Provider;

    public ValueTask<IEnumerable<TEntity>> QueryAsync(FilterDefinition<TEntity> filter, CancellationToken cancellationToken = default)
    {
        var session = _transactionProvider?.Invoke().Session;
        return session != null
            ? _baseSet.QueryAsync(filter, session, cancellationToken)
            : _baseSet.QueryAsync(filter, cancellationToken);
    }

    public ValueTask<IEnumerable<TEntity>> QueryAsync(Expression<Func<TEntity, bool>> filter, CancellationToken cancellationToken = default)
    {
        var session = _transactionProvider?.Invoke().Session;
        return session != null
            ? _baseSet.QueryAsync(filter, session, cancellationToken)
            : _baseSet.QueryAsync(filter, cancellationToken);
    }

    public ValueTask<IEnumerable<TEntity>> QueryAsync(FilterDefinition<TEntity> filter, IClientSessionHandle session, CancellationToken cancellationToken = default)
        => _baseSet.QueryAsync(filter, session, cancellationToken);

    public ValueTask<IEnumerable<TEntity>> QueryAsync(Expression<Func<TEntity, bool>> filter, IClientSessionHandle session, CancellationToken cancellationToken = default)
        => _baseSet.QueryAsync(filter, session, cancellationToken);

    private async Task InternalCommitAsync(DbSet<TEntity> mongoSet, IClientSessionHandle? session, CancellationToken cancellationToken = default)
    {
        var collection = mongoSet.Collection;
        var models = new List<WriteModel<TEntity>>();

        // Deduplicate added items (last one wins)
        var addedByUniqueId = _added
            .Where(doc => doc is not null)
            .GroupBy(doc => doc!.GetId(_idAccessor))
            .ToDictionary(g => g.Key, g => g.Last());

        // Deduplicate removed items
        var removedIds = _removed
            .Where(e => e is not null)
            .Select(e => e!.GetId(_idAccessor))
            .Distinct()
            .ToList();

        // Deduplicate updated items (last one wins)
        var updatedByUniqueId = _updated
            .Where(e => e is not null)
            .GroupBy(e => e!.GetId(_idAccessor))
            .ToDictionary(g => g.Key, g => g.Last());

        // Process removals
        foreach (var id in removedIds)
        {
            models.Add(new DeleteOneModel<TEntity>(Builders<TEntity>.Filter.Eq(_idFieldName, id)));
        }

        // Process updates and adds
        // We use ReplaceOne with IsUpsert = true to handle both updates and inserts safely in one round-trip.
        // We deduplicate between added and updated - if an ID is in both, we take the last state.
        var upserts = new Dictionary<object, TEntity>();
        foreach (var entry in addedByUniqueId) upserts[entry.Key] = entry.Value!;
        foreach (var entry in updatedByUniqueId) upserts[entry.Key] = entry.Value!;

        foreach (var entry in upserts)
        {
            // If it was also marked for removal, the order matters. 
            // In a true Unit of Work, if you Add then Remove, it's a no-op.
            // If you Remove then Add, it's an overwrite.
            // To match current behavior (Remove last), we added deletes first, so they will execute before replacements if ordered.
            // But usually we want to deduplicate here too.
            if (!removedIds.Contains(entry.Key))
            {
                models.Add(new ReplaceOneModel<TEntity>(Builders<TEntity>.Filter.Eq(_idFieldName, entry.Key), entry.Value) { IsUpsert = true });
            }
        }

        if (models.Count > 0)
        {
            if (session != null)
            {
                await collection.BulkWriteAsync(session, models, cancellationToken: cancellationToken);
            }
            else
            {
                await collection.BulkWriteAsync(models, cancellationToken: cancellationToken);
            }
        }
    }

    private Task InternalCommitAsync(InMemoryDbSet<TEntity> memSet, CancellationToken cancellationToken = default)
    {
        foreach (var entity in _added)
        {
            var existing = GetExistingFromInMemory(entity);
            if (existing != null)
            {
                memSet.Collection.Remove(existing);
            }

            memSet.Collection.Add(Clone(entity));
        }

        foreach (var entity in _removed)
        {
            var existing = GetExistingFromInMemory(entity);
            if (existing != null)
            {
                if (!memSet.Collection.Remove(existing))
                {
                    throw new InvalidOperationException($"Expected to delete {existing} but didn't... this is not supposed to happen.");
                }
            }
        }

        // updates -> remove + add for simplicity
        foreach (var entity in _updated)
        {
            var existing = GetExistingFromInMemory(entity);

            if (existing != null)
            {
                memSet.Collection.Remove(existing);
                memSet.Collection.Add(Clone(entity));
            }
        }

        TEntity? GetExistingFromInMemory(TEntity entity)
        {
            var id = _idAccessor(entity)
                ?? throw new InvalidOperationException("Cannot fetch entity Id without known Id property.");

            var existing = memSet
                .Collection
                .FirstOrDefault(x =>
                    x is not null
                    && _idAccessor(x) is { } existingId
                    && existingId.Equals(id));
            return existing;
        }

        return Task.CompletedTask;
    }

    private static TEntity Clone(TEntity source)
    {
        var json = JsonSerializer.Serialize(source);
        return JsonSerializer.Deserialize<TEntity>(json)!;
    }
}
