using System.Collections;
using System.Linq.Expressions;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;

// ReSharper disable ComplexConditionExpression

namespace MongoZen;

public class DbSet<T>(IMongoCollection<T> collection) : IDbSet<T>
{
    private readonly IQueryable<T> _collectionAsQueryable = collection.AsQueryable();

    public async ValueTask<IEnumerable<T>> QueryAsync(FilterDefinition<T> filter) =>
        await (await collection.FindAsync(filter)).ToListAsync();

    public async ValueTask<IEnumerable<T>> QueryAsync(Expression<Func<T, bool>> filter) =>
        await (await collection.FindAsync(Builders<T>.Filter.Where(filter))).ToListAsync();

    public async ValueTask<IEnumerable<T>> QueryAsync(FilterDefinition<T> filter, IClientSessionHandle session) =>
        await (await collection.FindAsync(session, filter)).ToListAsync();

    public async ValueTask<IEnumerable<T>> QueryAsync(Expression<Func<T, bool>> filter, IClientSessionHandle session) =>
        await (await collection.FindAsync(session, Builders<T>.Filter.Where(filter))).ToListAsync();

    public IEnumerator<T> GetEnumerator() => _collectionAsQueryable.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public Type ElementType => _collectionAsQueryable.ElementType;

    public Expression Expression => _collectionAsQueryable.Expression;

    public IQueryProvider Provider => _collectionAsQueryable.Provider;

    internal IMongoCollection<T> Collection => collection;

    public async Task RemoveById(object id)
    {
        var idProp = typeof(T).GetProperties().FirstOrDefault(p =>
            p.Name == "Id" ||
            p.GetCustomAttributes(typeof(BsonIdAttribute), true).Length != 0) ??
                     throw new InvalidOperationException("No Id or [BsonId] property found on type " + typeof(T).Name);

        var filter = Builders<T>.Filter.Eq("_id", id);
        var result = await collection.DeleteOneAsync(filter);

        if (result.DeletedCount != 1)
        {
            throw new InvalidOperationException($"Delete failed for entity with Id '{id}'.");
        }
    }
}
