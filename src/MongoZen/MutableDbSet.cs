using System.Collections;
using MongoDB.Driver;

// ReSharper disable ComplexConditionExpression
// ReSharper disable MethodTooLong

namespace MongoZen;

/// <summary>
/// Tracks pending changes for a DbSet and applies them as a unit.
/// </summary>
/// <typeparam name="TEntity">The entity type.</typeparam>
public class MutableDbSet<TEntity> : IMutableDbSet<TEntity>
{
    private readonly Conventions _conventions;
    private readonly IDbSet<TEntity> _baseSet;

    private readonly List<TEntity> _added = [];
    private readonly List<TEntity> _removed = [];
    private readonly List<TEntity> _updated = [];

    /// <summary>
    /// Initializes a new instance of the <see cref="MutableDbSet{TEntity}"/> class.
    /// </summary>
    /// <param name="baseSet">The underlying DbSet to operate on.</param>
    /// <param name="conventions">Optional conventions for ID resolution.</param>
    public MutableDbSet(IDbSet<TEntity> baseSet, Conventions? conventions = null)
    {
        _baseSet = baseSet;
        _conventions = conventions ?? new();

        EntityIdAccessor<TEntity>.SetConvention(_conventions.IdConvention);
    }

    /// <inheritdoc />
    public void Add(TEntity entity) => _added.Add(entity);

    /// <inheritdoc />
    public void Remove(TEntity entity) => _removed.Add(entity);

    /// <inheritdoc />
    public void Update(TEntity entity) => _updated.Add(entity);

    /// <inheritdoc />
    public IEnumerable<TEntity> GetAdded() => _added;

    /// <inheritdoc />
    public IEnumerable<TEntity> GetRemoved() => _removed;

    /// <inheritdoc />
    public IEnumerable<TEntity> GetUpdated() => _updated;

    /// <inheritdoc />
    public async Task CommitAsync()
    {
        switch (_baseSet)
        {
            case InMemoryDbSet<TEntity> memSet:
                await InternalCommitAsync(memSet);
                break;
            case DbSet<TEntity> mongoSet:
                await InternalCommitAsync(mongoSet);
                break;
            default:
                throw new NotSupportedException($"The type {_baseSet.GetType()} is not supported.");
        }

        _added.Clear();
        _removed.Clear();
        _updated.Clear();
    }

    private async Task InternalCommitAsync(DbSet<TEntity> mongoSet)
    {
        var collection = mongoSet.Collection;

        foreach (var entity in _updated)
        {
            if (!entity.TryGetId(out var id))
            {
                continue;
            }

            var filter = Builders<TEntity>.Filter.Eq("_id", id);
            var result = await collection.ReplaceOneAsync(filter, entity);

            // this means we are missing this document, so we mimic MongoDB driver and add it
            if (result.MatchedCount != 1 || result.ModifiedCount != 1)
            {
                var existing = _added.FirstOrDefault(addItem => addItem != null && addItem.GetId().Equals(entity.GetId()));

                // so we "override" the added item with updated item
                if (existing != null)
                {
                    _added.Remove(existing);
                }

                _added.Add(entity);
            }
        }

        if (_added.Count > 0)
        {
            var addedWithUniqueIds = _added.GroupBy(doc => doc!.GetId());
            var uniqueDocs = addedWithUniqueIds
                .Select(x => x.FirstOrDefault())
                .Where(x => x != null)
                .Cast<TEntity>();

            await collection.InsertManyAsync(uniqueDocs);

            foreach (var docGroup in addedWithUniqueIds.Where(group => group.Count() > 1))
            {
                var replacementDoc = docGroup.Last();
                await collection.ReplaceOneAsync(Builders<TEntity>.Filter.Eq("_id", docGroup.Key), replacementDoc);
            }
        }

        if (_removed.Count > 0)
        {
            var ids = _removed
                .Select(e => e.GetId())
                .Where(id => id != null)
                .ToList();

            var filter = Builders<TEntity>.Filter.In("_id", ids);
            await collection.DeleteManyAsync(filter); // in theory, could be non-existing IDs here
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
            if (!entity.TryGetId(out var id))
            {
                throw new InvalidOperationException($"Cannot fetch entity Id without known Id property.");
            }

            var existing = memSet
                .Collection
                .FirstOrDefault(x => x.GetId() != null && x.GetId()!.Equals(id));
            return existing;
        }

        return Task.CompletedTask;
    }

    // IQueryable passthrough
    /// <inheritdoc />
    public IEnumerator<TEntity> GetEnumerator() => _baseSet.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <inheritdoc />
    public Type ElementType => _baseSet.ElementType;

    /// <inheritdoc />
    public Expression Expression => _baseSet.Expression;

    /// <inheritdoc />
    public IQueryProvider Provider => _baseSet.Provider;

    /// <inheritdoc />
    public ValueTask<IEnumerable<TEntity>> QueryAsync(FilterDefinition<TEntity> filter) => _baseSet.QueryAsync(filter);

    /// <inheritdoc />
    public ValueTask<IEnumerable<TEntity>> QueryAsync(Expression<Func<TEntity, bool>> filter) => _baseSet.QueryAsync(filter);
}
