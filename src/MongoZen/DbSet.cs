using System.Collections;
using System.Data.Common;
using System.Linq.Expressions;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;

// ReSharper disable ComplexConditionExpression

namespace MongoZen;

public class DbSet<TEntity> : IDbSet<TEntity>
{
    private readonly IQueryable<TEntity> _collectionAsQueryable;
    private readonly Func<TEntity, object?> _idAccessor;
    private readonly Conventions _conventions;
    private readonly IMongoCollection<TEntity> _collection;

    public DbSet(IMongoCollection<TEntity> collection, Conventions conventions)
    {
        _conventions = conventions ?? new();
        _idAccessor = EntityIdAccessor<TEntity>.GetAccessor(_conventions.IdConvention);
        _collection = collection;
        _collectionAsQueryable = _collection.AsQueryable();
    }

    public async ValueTask<IEnumerable<TEntity>> QueryAsync(FilterDefinition<TEntity> filter) =>
        await (await _collection.FindAsync(filter)).ToListAsync();

    public async ValueTask<IEnumerable<TEntity>> QueryAsync(Expression<Func<TEntity, bool>> filter) =>
        await (await _collection.FindAsync(Builders<TEntity>.Filter.Where(filter))).ToListAsync();

    public async ValueTask<IEnumerable<TEntity>> QueryAsync(FilterDefinition<TEntity> filter, IClientSessionHandle session) =>
        await (await _collection.FindAsync(session, filter)).ToListAsync();

    public async ValueTask<IEnumerable<TEntity>> QueryAsync(Expression<Func<TEntity, bool>> filter, IClientSessionHandle session) =>
        await (await _collection.FindAsync(session, Builders<TEntity>.Filter.Where(filter))).ToListAsync();

    public IEnumerator<TEntity> GetEnumerator() => _collectionAsQueryable.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public Type ElementType => _collectionAsQueryable.ElementType;

    public Expression Expression => _collectionAsQueryable.Expression;

    public IQueryProvider Provider => _collectionAsQueryable.Provider;

    internal IMongoCollection<TEntity> Collection => _collection;

    public async Task Remove(TEntity entity)
    {
        var id = _idAccessor(entity) ?? throw new InvalidOperationException($"Entity of type {typeof(TEntity).Name} doesn't expose an Id.");
        var filter = Builders<TEntity>.Filter.Eq("_id", id);
        var result = await _collection.DeleteOneAsync(filter);

        if (result.DeletedCount != 1)
        {
            throw new InvalidOperationException($"Delete failed for entity with Id '{id}'.");
        }
    }
}
