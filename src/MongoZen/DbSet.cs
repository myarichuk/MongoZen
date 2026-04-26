using System.Collections;
using System.Data.Common;
using System.Linq.Expressions;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;

// ReSharper disable ComplexConditionExpression

namespace MongoZen;

public class DbSet<TEntity> : IDbSet<TEntity>, IInternalDbSet<TEntity> where TEntity : class
{
    private readonly Func<TEntity, object?> _idAccessor;
    private readonly string _idFieldName;
    private readonly Conventions _conventions;
    private readonly IMongoCollection<TEntity> _collection;

    public string CollectionName => _collection.CollectionNamespace.CollectionName;

    public DbSet(IMongoCollection<TEntity> collection, Conventions conventions)
    {
        _conventions = conventions ?? new();
        _idAccessor = EntityIdAccessor<TEntity>.GetAccessor(_conventions.IdConvention);
        _idFieldName = _conventions.IdConvention.ResolveIdProperty<TEntity>()?.Name ?? "_id";
        _collection = collection;
    }

    public async ValueTask<TEntity?> LoadAsync(object id, CancellationToken cancellationToken = default)
    {
        var filter = Builders<TEntity>.Filter.Eq(_idFieldName, id);
        return await (await _collection.FindAsync(filter, cancellationToken: cancellationToken)).FirstOrDefaultAsync(cancellationToken);
    }

    public IDbSet<TEntity> Include(Expression<Func<TEntity, object?>> path)
    {
        // TODO: Implement RavenDB-style Include (e.g. via $lookup or client-side batching)
        return this;
    }

    public IDbSet<TEntity> Include<TInclude>(Expression<Func<TEntity, object?>> path) where TInclude : class
    {
        // TODO: Implement RavenDB-style Include
        return this;
    }

    public async ValueTask<IEnumerable<TEntity>> QueryAsync(FilterDefinition<TEntity> filter, CancellationToken cancellationToken = default) =>
        await (await _collection.FindAsync(filter, cancellationToken: cancellationToken)).ToListAsync(cancellationToken);

    public async ValueTask<IEnumerable<TEntity>> QueryAsync(Expression<Func<TEntity, bool>> filter, CancellationToken cancellationToken = default) =>
        await (await _collection.FindAsync(Builders<TEntity>.Filter.Where(filter), cancellationToken: cancellationToken)).ToListAsync(cancellationToken);

    public async ValueTask<IEnumerable<TEntity>> QueryAsync(FilterDefinition<TEntity> filter, IClientSessionHandle session, CancellationToken cancellationToken = default) =>
        await (await _collection.FindAsync(session, filter, cancellationToken: cancellationToken)).ToListAsync(cancellationToken);

    public async ValueTask<IEnumerable<TEntity>> QueryAsync(Expression<Func<TEntity, bool>> filter, IClientSessionHandle session, CancellationToken cancellationToken = default) =>
        await (await _collection.FindAsync(session, Builders<TEntity>.Filter.Where(filter), cancellationToken: cancellationToken)).ToListAsync(cancellationToken);

    internal IMongoCollection<TEntity> Collection => _collection;

    public async Task Remove(TEntity entity)
    {
        var id = _idAccessor(entity) ?? throw new InvalidOperationException($"Entity of type {typeof(TEntity).Name} doesn't expose an Id.");
        var filter = Builders<TEntity>.Filter.Eq(_idFieldName, id);
        var result = await _collection.DeleteOneAsync(filter);

        if (result.DeletedCount != 1)
        {
            throw new InvalidOperationException($"Delete failed for entity with Id '{id}'.");
        }
    }

    async ValueTask IInternalDbSet<TEntity>.CommitAsync(IEnumerable<TEntity> added, IEnumerable<TEntity> removed, IEnumerable<object> removedIds, IEnumerable<TEntity> updated, IClientSessionHandle? session, CancellationToken cancellationToken)
    {
        var models = new List<WriteModel<TEntity>>();

        // Track IDs being removed for O(1) exclusion check.
        var removedIdSet = new HashSet<object>(removed
            .Where(e => e is not null)
            .Select(e => e!.GetId(_idAccessor)!)
            .Concat(removedIds));

        if (removedIdSet.Count > 0)
        {
            models.Add(new DeleteManyModel<TEntity>(Builders<TEntity>.Filter.In(_idFieldName, removedIdSet)));
        }

        // Use a single dictionary to deduplicate all "Upserts" (Added + Updated).
        // If an ID appears multiple times, the last one wins, matching the previous behavior.
        var upserts = new Dictionary<object, TEntity>();
        
        foreach (var entity in added)
        {
            if (entity == null) continue;
            var id = entity.GetId(_idAccessor);
            if (id != null && !removedIdSet.Contains(id))
            {
                upserts[id] = entity;
            }
        }

        foreach (var entity in updated)
        {
            if (entity == null) continue;
            var id = entity.GetId(_idAccessor);
            if (id != null && !removedIdSet.Contains(id))
            {
                upserts[id] = entity;
            }
        }

        foreach (var entry in upserts)
        {
            // We use ReplaceOne + IsUpsert=true because it handles both brand-new 
            // entities and existing ones correctly in a single operation.
            models.Add(new ReplaceOneModel<TEntity>(Builders<TEntity>.Filter.Eq(_idFieldName, entry.Key), entry.Value) { IsUpsert = true });
        }

        if (models.Count > 0)
        {
            if (session != null)
            {
                await _collection.BulkWriteAsync(session, models, cancellationToken: cancellationToken);
            }
            else
            {
                await _collection.BulkWriteAsync(models, cancellationToken: cancellationToken);
            }
        }
    }
}

