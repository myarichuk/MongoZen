using System.Collections;
using System.Data.Common;
using System.Linq.Expressions;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;
using MongoZen.Collections;

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
        // TODO: Implement RavenDB-style Include
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
        PooledDictionary<DocId, (TEntity Entity, bool IsDirty)> upsertBuffer,
        PooledHashSet<object> rawIdBuffer,
        PooledList<WriteModel<TEntity>> modelBuffer,
        Func<TEntity, IntPtr, UpdateDefinition<TEntity>?>? extractor,
        ISessionTracker tracker,
        TransactionContext transaction, 
        SharpArena.Allocators.ArenaAllocator arena,
        CancellationToken cancellationToken)
    {
        modelBuffer.Clear();
        upsertBuffer.Clear();
        rawIdBuffer.Clear();

        var dedupeBuffer = new ArenaHashSet<DocId>(arena, 128);

        // 1. Process Removals
        BuildDeleteModels(removed, removedIds, ref dedupeBuffer, rawIdBuffer, modelBuffer);

        // 2. Process Added
        BuildInsertModels(added, ref dedupeBuffer, upsertBuffer, modelBuffer);
        upsertBuffer.Clear();

        // 3. Process Updated/Dirty
        CollectUpdates(updated, dirty, ref dedupeBuffer, upsertBuffer);

        // 4. Apply Versions and Execute
        if (upsertBuffer.Count > 0 || modelBuffer.Count > 0)
        {
            var versionCtx = ResolveVersionContext();
            var versionMap = new PooledDictionary<DocId, (object RawId, long Version)>(upsertBuffer.Count);
            var updateCount = 0;

            try
            {
                foreach (var entry in upsertBuffer)
                {
                    var model = PrepareUpdateOrReplaceModel(entry.Key, entry.Value.Entity, entry.Value.IsDirty, versionCtx, extractor, tracker, versionMap);
                    modelBuffer.Add(model);
                    if (versionCtx.IsValid && model is not ReplaceOneModel<TEntity> { IsUpsert: true })
                    {
                        updateCount++;
                    }
                }

                if (modelBuffer.Count == 0) return;

                BulkWriteResult result = transaction.Session != null 
                    ? await _collection.BulkWriteAsync(transaction.Session, modelBuffer, cancellationToken: cancellationToken)
                    : await _collection.BulkWriteAsync(modelBuffer, cancellationToken: cancellationToken);

                if (updateCount > 0 && result.MatchedCount < updateCount)
                {
                    var failedIds = await FindConcurrencyConflictsAsync(versionMap, versionCtx.ElementName!, transaction.Session, cancellationToken);
                    throw new ConcurrencyException($"Optimistic concurrency check failed. Expected {updateCount} matches, but got {result.MatchedCount}.", failedIds);
                }
            }
            catch
            {
                if (versionCtx.IsValid) RevertVersions(upsertBuffer, versionMap, versionCtx.Setter!);
                throw;
            }
            finally
            {
                versionMap.Dispose();
            }
        }
    }

    private void BuildDeleteModels(IEnumerable<TEntity> removed, IEnumerable<object> removedIds, ref ArenaHashSet<DocId> dedupeBuffer, PooledHashSet<object> rawIdBuffer, PooledList<WriteModel<TEntity>> modelBuffer)
    {
        foreach (var entity in removed)
        {
            if (entity == null) continue;
            var rawId = entity.GetId(_idAccessor);
            if (rawId != null && dedupeBuffer.Add(DocId.From(rawId)))
            {
                rawIdBuffer.Add(rawId);
            }
        }
        foreach (var rawId in removedIds)
        {
            if (rawId != null && dedupeBuffer.Add(DocId.From(rawId)))
            {
                rawIdBuffer.Add(rawId);
            }
        }

        if (rawIdBuffer.Count > 0)
        {
            modelBuffer.Add(new DeleteManyModel<TEntity>(Builders<TEntity>.Filter.In(_idFieldName, rawIdBuffer)));
        }
    }

    private void BuildInsertModels(IEnumerable<TEntity> added, ref ArenaHashSet<DocId> dedupeBuffer, PooledDictionary<DocId, (TEntity Entity, bool IsDirty)> upsertBuffer, PooledList<WriteModel<TEntity>> modelBuffer)
    {
        foreach (var entity in added)
        {
            if (entity == null) continue;
            var rawId = entity.GetId(_idAccessor);
            if (rawId != null && !dedupeBuffer.Contains(DocId.From(rawId)))
            {
                upsertBuffer.AddOrUpdate(DocId.From(rawId), (entity, false));
            }
        }

        foreach (var entry in upsertBuffer)
        {
            modelBuffer.Add(new InsertOneModel<TEntity>(entry.Value.Entity));
            dedupeBuffer.Add(entry.Key); 
        }
    }

    private void CollectUpdates(IEnumerable<TEntity> updated, IEnumerable<TEntity> dirty, ref ArenaHashSet<DocId> dedupeBuffer, PooledDictionary<DocId, (TEntity Entity, bool IsDirty)> upsertBuffer)
    {
        foreach (var entity in updated)
        {
            if (entity == null) continue;
            var rawId = entity.GetId(_idAccessor);
            if (rawId != null && !dedupeBuffer.Contains(DocId.From(rawId)))
            {
                upsertBuffer.AddOrUpdate(DocId.From(rawId), (entity, false));
            }
        }
        foreach (var entity in dirty)
        {
            if (entity == null) continue;
            var rawId = entity.GetId(_idAccessor);
            if (rawId != null && !dedupeBuffer.Contains(DocId.From(rawId)))
            {
                upsertBuffer.AddOrUpdate(DocId.From(rawId), (entity, true));
            }
        }
    }

    private WriteModel<TEntity> PrepareUpdateOrReplaceModel(
        DocId docId, 
        TEntity entity, 
        bool isDirty, 
        VersionContext versionCtx,
        Func<TEntity, IntPtr, UpdateDefinition<TEntity>?>? extractor,
        ISessionTracker tracker,
        PooledDictionary<DocId, (object RawId, long Version)> versionMap)
    {
        var rawId = entity.GetId(_idAccessor);
        var filter = Builders<TEntity>.Filter.Eq(_idFieldName, rawId);

        if (versionCtx.IsValid && rawId != null)
        {
            var currentVersion = versionCtx.Getter!(entity);
            versionMap[docId] = (rawId, currentVersion);

            filter = Builders<TEntity>.Filter.And(filter, Builders<TEntity>.Filter.Eq(versionCtx.ElementName!, currentVersion));
            versionCtx.Setter!(entity, currentVersion + 1);

            UpdateDefinition<TEntity>? update = null;
            if (isDirty && extractor != null && tracker != null && tracker.TryGetShadowPtr<TEntity>(rawId, out var shadowPtr))
            {
                update = extractor(entity, shadowPtr);
            }

            if (update != null)
            {
                update = Builders<TEntity>.Update.Combine(update, Builders<TEntity>.Update.Set(versionCtx.ElementName!, currentVersion + 1));
                var updateModel = new UpdateOneModel<TEntity>(filter, update);
                updateModel.IsUpsert = false;
                return updateModel;
            }
            
            var replaceModel = new ReplaceOneModel<TEntity>(filter, entity);
            replaceModel.IsUpsert = false;
            return replaceModel;
        }

        var finalReplaceModel = new ReplaceOneModel<TEntity>(filter, entity);
        finalReplaceModel.IsUpsert = true;
        return finalReplaceModel;
    }

    private VersionContext ResolveVersionContext()
    {
        var getter = ConcurrencyVersionAccessor<TEntity>.GetGetter(_conventions.ConcurrencyPropertyName);
        var setter = ConcurrencyVersionAccessor<TEntity>.GetSetter(_conventions.ConcurrencyPropertyName);
        var elementName = ConcurrencyVersionAccessor<TEntity>.GetElementName(_conventions.ConcurrencyPropertyName);
        return new VersionContext(getter, setter, elementName);
    }

    private void RevertVersions(PooledDictionary<DocId, (TEntity Entity, bool IsDirty)> upsertBuffer, PooledDictionary<DocId, (object RawId, long Version)> versionMap, Action<TEntity, long> versionSetter)
    {
        foreach (var entry in upsertBuffer)
        {
            var entity = entry.Value.Entity;
            var rawId = entity.GetId(_idAccessor);
            if (rawId != null && versionMap.TryGetValue(DocId.From(rawId), out var original))
            {
                versionSetter(entity, original.Version);
            }
        }
    }

    private async Task<List<object>> FindConcurrencyConflictsAsync(PooledDictionary<DocId, (object RawId, long Version)> versionMap, string concurrencyElementName, IClientSessionHandle? session, CancellationToken ct)
    {
        var ids = new List<object>(versionMap.Count);
        foreach (var kvp in versionMap)
        {
            ids.Add(kvp.Value.RawId);
        }

        var projection = Builders<TEntity>.Projection.Include(_idFieldName).Include(concurrencyElementName);
        
        var filter = Builders<TEntity>.Filter.In(_idFieldName, ids);
        var docs = session != null
            ? await _collection.Find(session, filter).Project(projection).ToListAsync(ct)
            : await _collection.Find(filter).Project(projection).ToListAsync(ct);

        using var foundIds = new PooledHashSet<DocId>(docs.Count);
        var conflicts = new List<object>();

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

        foreach (var entry in versionMap)
        {
            if (!foundIds.Contains(entry.Key))
            {
                conflicts.Add(entry.Value.RawId);
            }
        }

        return conflicts;
    }

    private readonly struct VersionContext
    {
        public readonly Func<TEntity, long>? Getter;
        public readonly Action<TEntity, long>? Setter;
        public readonly string? ElementName;
        public bool IsValid => Getter != null && Setter != null && ElementName != null;

        public VersionContext(Func<TEntity, long>? getter, Action<TEntity, long>? setter, string? elementName)
        {
            Getter = getter;
            Setter = setter;
            ElementName = elementName;
        }
    }
}
