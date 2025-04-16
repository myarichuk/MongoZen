using System.Collections;
using MongoDB.Driver;
using System.Linq.Expressions;
using System.Threading.Tasks;
using System;
using System.Linq;
using MongoDB.Bson.Serialization.Attributes;
// ReSharper disable ComplexConditionExpression

namespace Library;

public class DbSet<T> : IDbSet<T>
{
    private readonly IQueryable<T> _collectionAsQueryable;
    private readonly IMongoCollection<T> _collection;

    public DbSet(IMongoCollection<T> collection)
    {
        _collection = collection;
        _collectionAsQueryable = collection.AsQueryable();
    }

    public async ValueTask<IEnumerable<T>> QueryAsync(FilterDefinition<T> filter) =>
        await (await _collection.FindAsync(filter)).ToListAsync();

    public async ValueTask<IEnumerable<T>> QueryAsync(Expression<Func<T, bool>> filter) =>
        await (await _collection.FindAsync(Builders<T>.Filter.Where(filter))).ToListAsync();

    public IEnumerator<T> GetEnumerator() => _collectionAsQueryable.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public Type ElementType => _collectionAsQueryable.ElementType;

    public Expression Expression => _collectionAsQueryable.Expression;

    public IQueryProvider Provider => _collectionAsQueryable.Provider;

    internal IMongoCollection<T> Collection => _collection;

    public async Task RemoveById(object id)
    {
        var idProp = typeof(T).GetProperties().FirstOrDefault(p =>
            p.Name == "Id" ||
            p.GetCustomAttributes(typeof(BsonIdAttribute), true).Length != 0) ??
                     throw new InvalidOperationException("No Id or [BsonId] property found on type " + typeof(T).Name);

        var filter = Builders<T>.Filter.Eq("_id", id);
        var result = await _collection.DeleteOneAsync(filter);

        if (result.DeletedCount != 1)
        {
            throw new InvalidOperationException($"Delete failed for entity with Id '{id}'.");
        }
    }
}