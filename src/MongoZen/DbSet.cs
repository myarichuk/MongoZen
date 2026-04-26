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

    async ValueTask IInternalDbSet<TEntity>.CommitAsync(
        IEnumerable<TEntity> added, 
        IEnumerable<TEntity> removed, 
        IEnumerable<object> removedIds, 
        IEnumerable<TEntity> updated, 
        IEnumerable<TEntity> dirty, 
        SharpArena.Allocators.ArenaAllocator arena,
        Dictionary<DocId, TEntity> upsertBuffer,
        HashSet<DocId> dedupeBuffer,
        HashSet<object> rawIdBuffer,
        List<WriteModel<TEntity>> modelBuffer,
        IClientSessionHandle? session, 
        CancellationToken cancellationToken)
    {
        modelBuffer.Clear();
        dedupeBuffer.Clear();
        upsertBuffer.Clear();
        rawIdBuffer.Clear();

        // 1. Process Removals (Deduplicate via DocId, store raw for filter)
        foreach (var entity in removed)
        {
            if (entity == null) continue;
            var rawId = entity.GetId(_idAccessor);
            if (rawId == null) continue;
            var docId = DocId.From(rawId);
            if (dedupeBuffer.Add(docId))
            {
                rawIdBuffer.Add(rawId);
            }
        }
        foreach (var rawId in removedIds)
        {
            if (rawId == null) continue;
            var docId = DocId.From(rawId);
            if (dedupeBuffer.Add(docId))
            {
                rawIdBuffer.Add(rawId);
            }
        }

        if (rawIdBuffer.Count > 0)
        {
            modelBuffer.Add(new DeleteManyModel<TEntity>(Builders<TEntity>.Filter.In(_idFieldName, rawIdBuffer)));
        }

        // 2. Process Added (Deduplicate via DocId, skip if removed)
        foreach (var entity in added)
        {
            if (entity == null) continue;
            var rawId = entity.GetId(_idAccessor);
            if (rawId == null) continue;
            var docId = DocId.From(rawId);
            if (!dedupeBuffer.Contains(docId))
            {
                upsertBuffer[docId] = entity;
            }
        }

        foreach (var entry in upsertBuffer)
        {
            modelBuffer.Add(new InsertOneModel<TEntity>(entry.Value));
            dedupeBuffer.Add(entry.Key); // Prevent updates for this ID
        }
        upsertBuffer.Clear();

        // 3. Process Updated/Dirty (Last one wins)
        foreach (var entity in updated)
        {
            if (entity == null) continue;
            var rawId = entity.GetId(_idAccessor);
            if (rawId == null) continue;
            var docId = DocId.From(rawId);
            if (!dedupeBuffer.Contains(docId))
            {
                upsertBuffer[docId] = entity;
            }
        }
        foreach (var entity in dirty)
        {
            if (entity == null) continue;
            var rawId = entity.GetId(_idAccessor);
            if (rawId == null) continue;
            var docId = DocId.From(rawId);
            if (!dedupeBuffer.Contains(docId))
            {
                upsertBuffer[docId] = entity;
            }
        }

        foreach (var entry in upsertBuffer)
        {
            // We need the raw ID for the Eq filter. Since TEntity is here, we can re-extract it.
            var rawId = entry.Value.GetId(_idAccessor);
            modelBuffer.Add(new ReplaceOneModel<TEntity>(Builders<TEntity>.Filter.Eq(_idFieldName, rawId), entry.Value) { IsUpsert = true });
        }

        if (modelBuffer.Count > 0)
        {
            if (session != null)
            {
                await _collection.BulkWriteAsync(session, modelBuffer, cancellationToken: cancellationToken);
            }
            else
            {
                await _collection.BulkWriteAsync(modelBuffer, cancellationToken: cancellationToken);
            }
        }
    }
}
