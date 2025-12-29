using System.Collections;
using MongoDB.Driver;
using MongoDB.Bson.Serialization.Attributes;

// ReSharper disable ComplexConditionExpression

namespace MongoZen;

/// <summary>
/// Represents a MongoDB-backed set of entities that supports LINQ queries and filter-based lookups.
/// </summary>
/// <typeparam name="T">The entity type stored in the collection.</typeparam>
public class DbSet<T>(IMongoCollection<T> collection) : IDbSet<T>
{
    private readonly IQueryable<T> _collectionAsQueryable = collection.AsQueryable();

    /// <inheritdoc />
    public async ValueTask<IEnumerable<T>> QueryAsync(FilterDefinition<T> filter) =>
        await (await collection.FindAsync(filter)).ToListAsync();

    /// <inheritdoc />
    public async ValueTask<IEnumerable<T>> QueryAsync(Expression<Func<T, bool>> filter) =>
        await (await collection.FindAsync(Builders<T>.Filter.Where(filter))).ToListAsync();

    /// <inheritdoc />
    public IEnumerator<T> GetEnumerator() => _collectionAsQueryable.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <inheritdoc />
    public Type ElementType => _collectionAsQueryable.ElementType;

    /// <inheritdoc />
    public Expression Expression => _collectionAsQueryable.Expression;

    /// <inheritdoc />
    public IQueryProvider Provider => _collectionAsQueryable.Provider;

    internal IMongoCollection<T> Collection => collection;

    /// <summary>
    /// Removes a single entity by its identifier and throws if the delete does not affect exactly one document.
    /// </summary>
    /// <param name="id">The identifier value to delete.</param>
    /// <exception cref="InvalidOperationException">
    /// Thrown when no ID property is found or when the delete does not affect exactly one document.
    /// </exception>
    public async Task RemoveById(object id)
    {
        var hasIdProperty = typeof(T).GetProperties().Any(p =>
            p.Name == "Id" ||
            p.GetCustomAttributes(typeof(BsonIdAttribute), true).Length != 0);
        if (!hasIdProperty)
        {
            throw new InvalidOperationException("No Id or [BsonId] property found on type " + typeof(T).Name);
        }

        var filter = Builders<T>.Filter.Eq("_id", id);
        var result = await collection.DeleteOneAsync(filter);

        if (result.DeletedCount != 1)
        {
            throw new InvalidOperationException($"Delete failed for entity with Id '{id}'.");
        }
    }
}
