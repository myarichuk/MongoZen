using System.Collections;
using MongoDB.Driver;
using MongoDB.Bson.Serialization.Attributes;

// ReSharper disable ComplexConditionExpression
// ReSharper disable MethodTooLong

namespace Library;

public class MutableDbSet<T> : IMutableDbSet<T>
{
    private readonly IDbSet<T> _baseSet;

    private readonly List<T> _added = new();
    private readonly List<T> _removed = new();
    private readonly List<T> _updated = new();

    public MutableDbSet(IDbSet<T> baseSet) => _baseSet = baseSet;

    public void Add(T entity) => _added.Add(entity);

    public void Remove(T entity) => _removed.Add(entity);

    public void Update(T entity) => _updated.Add(entity);

    public IEnumerable<T> GetAdded() => _added;

    public IEnumerable<T> GetRemoved() => _removed;

    public IEnumerable<T> GetUpdated() => _updated;

    public async Task CommitAsync()
    {
        switch (_baseSet)
        {
            case InMemoryDbSet<T> memSet:
                await InternalCommitAsync(memSet);
                break;
            case DbSet<T> mongoSet:
                await InternalCommitAsync(mongoSet);
                break;
            default:
                throw new NotSupportedException($"The type {_baseSet.GetType()} is not supported.");
        }

        _added.Clear();
        _removed.Clear();
        _updated.Clear();
    }

    private async Task InternalCommitAsync(DbSet<T> mongoSet)
    {
        var collection = mongoSet.Collection;

        if (_added.Count > 0)
        {
            await collection.InsertManyAsync(_added);
        }

        foreach (var entity in _updated)
        {
            var idProp = typeof(T).GetProperties().FirstOrDefault(p => p.GetCustomAttributes(typeof(BsonIdAttribute), true).Any())
                         ?? typeof(T).GetProperty("Id");

            if (idProp == null)
            {
                continue;
            }

            var id = idProp.GetValue(entity);
            if (id == null)
            {
                continue;
            }

            var filter = Builders<T>.Filter.Eq("_id", id);
            var result = await collection.ReplaceOneAsync(filter, entity);
            if (result.MatchedCount != 1 || result.ModifiedCount != 1)
            {
                throw new InvalidOperationException($"Update failed for entity with Id '{id}'.");
            }
        }

        foreach (var entity in _removed)
        {
            var idProp = typeof(T).GetProperties().FirstOrDefault(p => p.GetCustomAttributes(typeof(BsonIdAttribute), true).Any())
                         ?? typeof(T).GetProperty("Id");

            if (idProp == null)
            {
                continue;
            }

            var id = idProp.GetValue(entity);
            if (id == null)
            {
                continue;
            }

            var filter = Builders<T>.Filter.Eq("_id", id);
            var result = await collection.DeleteOneAsync(filter);
            if (result.DeletedCount != 1)
            {
                throw new InvalidOperationException($"Delete failed for entity with Id '{id}'.");
            }
        }
    }

    private async Task InternalCommitAsync(InMemoryDbSet<T> memSet)
    {
        foreach (var entity in _added)
        {
            memSet.Add(entity);
        }

        foreach (var entity in _removed)
        {
            memSet.Remove(entity);
        }

        // updates -> remove + add for simplicity
        foreach (var entity in _updated)
        {
            var idProp = typeof(T).GetProperties().FirstOrDefault(p => p.GetCustomAttributes(typeof(BsonIdAttribute), true).Any())
                         ?? typeof(T).GetProperty("Id");

            if (idProp == null)
            {
                continue;
            }

            var id = idProp.GetValue(entity);
            var existing = (await memSet.QueryAsync(x => idProp.GetValue(x).Equals(id) == true)).FirstOrDefault();
            if (existing != null)
            {
                memSet.Remove(existing);
                memSet.Add(entity);
            }
        }
    }

    // IQueryable passthrough
    public IEnumerator<T> GetEnumerator() => _baseSet.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public Type ElementType => _baseSet.ElementType;

    public Expression Expression => _baseSet.Expression;

    public IQueryProvider Provider => _baseSet.Provider;

    public ValueTask<IEnumerable<T>> QueryAsync(FilterDefinition<T> filter) => _baseSet.QueryAsync(filter);

    public ValueTask<IEnumerable<T>> QueryAsync(Expression<Func<T, bool>> filter) => _baseSet.QueryAsync(filter);
}