using System.Collections;
using MongoDB.Driver;

// ReSharper disable ComplexConditionExpression
// ReSharper disable MethodTooLong

namespace MongoZen;

public class MutableDbSet<TEntity> : IMutableDbSet<TEntity>
{
    private readonly Conventions _conventions;
    private readonly IDbSet<TEntity> _baseSet;
    private readonly Func<TEntity, object?> _idAccessor;

    private readonly List<TEntity> _added = [];
    private readonly List<TEntity> _removed = [];
    private readonly List<TEntity> _updated = [];

    public MutableDbSet(IDbSet<TEntity> baseSet, Conventions? conventions = null)
    {
        _baseSet = baseSet;
        _conventions = conventions ?? new();
        _idAccessor = EntityIdAccessor<TEntity>.GetAccessor(_conventions.IdConvention);
    }

    public void Add(TEntity entity) => _added.Add(entity);

    public void Remove(TEntity entity) => _removed.Add(entity);

    public void Update(TEntity entity) => _updated.Add(entity);

    public IEnumerable<TEntity> GetAdded() => _added;

    public IEnumerable<TEntity> GetRemoved() => _removed;

    public IEnumerable<TEntity> GetUpdated() => _updated;

    public async Task CommitAsync(TransactionContext transaction)
    {
        if (!transaction.IsActive)
        {
            throw new InvalidOperationException("A transaction is required to commit changes. Call BeginTransaction() on the session and pass the transaction to CommitAsync().");
        }

        switch (_baseSet)
        {
            case InMemoryDbSet<TEntity> memSet:
                if (!transaction.IsInMemoryTransaction)
                {
                    throw new InvalidOperationException("In-memory commits require an in-memory transaction.");
                }

                await InternalCommitAsync(memSet);
                break;
            case DbSet<TEntity> mongoSet:
                if (transaction.Session == null)
                {
                    throw new InvalidOperationException("MongoDB commits require a session-bound transaction.");
                }

                if (!transaction.Session.IsInTransaction)
                {
                    throw new InvalidOperationException("MongoDB commits require an active transaction.");
                }

                await InternalCommitAsync(mongoSet, transaction.Session);
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
    public IEnumerator<TEntity> GetEnumerator() => _baseSet.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public Type ElementType => _baseSet.ElementType;

    public Expression Expression => _baseSet.Expression;

    public IQueryProvider Provider => _baseSet.Provider;

    public ValueTask<IEnumerable<TEntity>> QueryAsync(FilterDefinition<TEntity> filter) => _baseSet.QueryAsync(filter);

    public ValueTask<IEnumerable<TEntity>> QueryAsync(Expression<Func<TEntity, bool>> filter) => _baseSet.QueryAsync(filter);

    public ValueTask<IEnumerable<TEntity>> QueryAsync(FilterDefinition<TEntity> filter, IClientSessionHandle session)
        => _baseSet.QueryAsync(filter, session);

    public ValueTask<IEnumerable<TEntity>> QueryAsync(Expression<Func<TEntity, bool>> filter, IClientSessionHandle session)
        => _baseSet.QueryAsync(filter, session);

    private async Task InternalCommitAsync(DbSet<TEntity> mongoSet, IClientSessionHandle session)
    {
        var collection = mongoSet.Collection;
        var writes = new List<WriteModel<TEntity>>();

        // Handle removals
        if (_removed.Count > 0)
        {
            var ids = _removed
                .Where(e => e is not null)
                .Select(e => _idAccessor(e!) ?? throw new InvalidOperationException(
                    $"Object of type {typeof(TEntity).Name} doesn't expose an Id."))
                .ToList();

            var filter = Builders<TEntity>.Filter.In("_id", ids);
            writes.Add(new DeleteManyModel<TEntity>(filter));
        }

        // Handle updates and adds together by determining the final state per ID
        var finalState = new Dictionary<object, TEntity>();

        // Adds come first
        foreach (var entity in _added)
        {
            if (entity is null)
            {
                continue;
            }

            var id = _idAccessor(entity) ?? throw new InvalidOperationException(
                $"Object of type {typeof(TEntity).Name} doesn't expose an Id.");

            finalState[id] = entity;
        }

        // Updates override adds, and also apply on top of existing DB documents
        foreach (var entity in _updated)
        {
            if (entity is null)
            {
                continue;
            }

            var id = _idAccessor(entity);
            if (id is null)
            {
                continue;
            }

            finalState[id] = entity;
        }

        foreach (var kvp in finalState)
        {
            var filter = Builders<TEntity>.Filter.Eq("_id", kvp.Key);
            // ReplaceOneModel with IsUpsert = true handles both Add (Insert) and Update (Replace)
            var replaceModel = new ReplaceOneModel<TEntity>(filter, kvp.Value)
            {
                IsUpsert = true,
            };
            writes.Add(replaceModel);
        }

        if (writes.Count > 0)
        {
            await collection.BulkWriteAsync(session, writes);
        }
    }

    private Task InternalCommitAsync(InMemoryDbSet<TEntity> memSet)
    {
        foreach (var entity in _added)
        {
            var existing = GetExistingFromInMemory(entity);
            if (existing != null)
            {
                memSet.Collection.Remove(existing);
            }

            memSet.Collection.Add(entity);
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
                memSet.Collection.Add(entity);
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
}
