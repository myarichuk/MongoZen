using System.Collections;
using System.Data.Common;
using System.Linq.Expressions;
using MongoDB.Bson;
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
        MongoZen.Collections.PooledDictionary<DocId, TEntity> upsertBuffer,
        MongoZen.Collections.PooledHashSet<object> rawIdBuffer,
        MongoZen.Collections.PooledList<WriteModel<TEntity>> modelBuffer,
        TransactionContext transaction, 
        SharpArena.Allocators.ArenaAllocator arena,
        CancellationToken cancellationToken)
    {
        modelBuffer.Clear();
        upsertBuffer.Clear();
        rawIdBuffer.Clear();

        var dedupeBuffer = new MongoZen.Collections.ArenaHashSet<DocId>(arena, 128);

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
            // Builders<T>.Filter.In handles IEnumerable<T>
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
                upsertBuffer.AddOrUpdate(docId, entity);
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
                upsertBuffer.AddOrUpdate(docId, entity);
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
                upsertBuffer.AddOrUpdate(docId, entity);
            }
        }



        Func<TEntity, long>? versionGetter = null;
        Action<TEntity, long>? versionSetter = null;
        string? concurrencyElementName = null;
        var versionAccessorsResolved = false;

        var updateCount = 0;
        bool hasVersionMap = false;
        var versionMap = new MongoZen.Collections.PooledDictionary<DocId, (object RawId, long Version)>();

        foreach (var entry in upsertBuffer)
        {
            var entity = entry.Value;
            var rawId = entity.GetId(_idAccessor);
            var filter = Builders<TEntity>.Filter.Eq(_idFieldName, rawId);

            if (!versionAccessorsResolved)
            {
                versionGetter = ConcurrencyVersionAccessor<TEntity>.GetGetter(_conventions.ConcurrencyPropertyName);
                versionSetter = ConcurrencyVersionAccessor<TEntity>.GetSetter(_conventions.ConcurrencyPropertyName);
                concurrencyElementName = ConcurrencyVersionAccessor<TEntity>.GetElementName(_conventions.ConcurrencyPropertyName);
                versionAccessorsResolved = true;
            }

            if (versionGetter != null && versionSetter != null && concurrencyElementName != null && rawId != null)
            {
                if (!hasVersionMap)
                {
                    versionMap = new MongoZen.Collections.PooledDictionary<DocId, (object RawId, long Version)>(upsertBuffer.Count);
                    hasVersionMap = true;
                }
                
                var currentVersion = versionGetter(entity);
                versionMap[DocId.From(rawId)] = (rawId, currentVersion);

                filter = Builders<TEntity>.Filter.And(filter, Builders<TEntity>.Filter.Eq(concurrencyElementName, currentVersion));
                
                versionSetter(entity, currentVersion + 1);
                
                modelBuffer.Add(new ReplaceOneModel<TEntity>(filter, entity) { IsUpsert = false });
                updateCount++;
            }
            else
            {
                modelBuffer.Add(new ReplaceOneModel<TEntity>(filter, entity) { IsUpsert = true });
            }
        }

        if (modelBuffer.Count > 0)
        {
            try
            {
                BulkWriteResult result;
                if (transaction.Session != null)
                {
                    result = await _collection.BulkWriteAsync(transaction.Session, modelBuffer, cancellationToken: cancellationToken);
                }
                else
                {
                    result = await _collection.BulkWriteAsync(modelBuffer, cancellationToken: cancellationToken);
                }

                if (updateCount > 0 && result.MatchedCount < updateCount)
                {
                    var failedIds = await FindConcurrencyConflictsAsync(versionMap, concurrencyElementName!, transaction.Session, cancellationToken);
                    throw new ConcurrencyException($"Optimistic concurrency check failed. Expected {updateCount} matches, but got {result.MatchedCount}.", failedIds);
                }
            }
            catch
            {
                // Revert versions if bulk write failed or if concurrency conflict was thrown
                if (hasVersionMap && versionSetter != null)
                {
                    foreach (var entry in upsertBuffer)
                    {
                        var rawId = entry.Value.GetId(_idAccessor);
                        if (rawId != null && versionMap.TryGetValue(DocId.From(rawId), out var original))
                        {
                            versionSetter(entry.Value, original.Version);
                        }
                    }
                }
                throw;
            }
            finally
            {
                if (hasVersionMap) versionMap.Dispose();
            }
        }
    }

    private async Task<List<object>> FindConcurrencyConflictsAsync(MongoZen.Collections.PooledDictionary<DocId, (object RawId, long Version)> versionMap, string concurrencyElementName, IClientSessionHandle? session, CancellationToken ct)
    {
        var ids = new List<object>(versionMap.Count);
        foreach (var entry in versionMap)
        {
            ids.Add(entry.Value.RawId);
        }

        var projection = Builders<TEntity>.Projection.Include(_idFieldName).Include(concurrencyElementName);
        
        List<BsonDocument> docs;
        var filter = Builders<TEntity>.Filter.In(_idFieldName, ids);
        if (session != null)
            docs = await _collection.Find(session, filter).Project(projection).ToListAsync(ct);
        else
            docs = await _collection.Find(filter).Project(projection).ToListAsync(ct);

        using var foundIds = new MongoZen.Collections.PooledHashSet<DocId>(docs.Count);
        using var conflicts = new MongoZen.Collections.PooledList<object>(ids.Count);

        foreach (var doc in docs)
        {
            var rawIdFromDb = BsonTypeMapper.MapToDotNetValue(doc["_id"]);
            var docId = DocId.From(rawIdFromDb);
            foundIds.Add(docId);

            if (versionMap.TryGetValue(docId, out var expected))
            {
                var actualVersion = BsonTypeMapper.MapToDotNetValue(doc[concurrencyElementName]);
                if (Convert.ToInt64(actualVersion) != expected.Version)
                {
                    conflicts.Add(expected.RawId);
                }
            }
        }

        // Also include IDs that were completely missing (deleted)
        foreach (var entry in versionMap)
        {
            if (!foundIds.Contains(entry.Key))
            {
                conflicts.Add(entry.Value.RawId);
            }
        }

        return conflicts.ToList();
    }
}
