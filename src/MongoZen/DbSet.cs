using System.Collections;
using System.Data.Common;
using System.Linq.Expressions;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;
using MongoZen.Collections;
using SharpArena.Collections;

// ReSharper disable ComplexConditionExpression

namespace MongoZen;

public class DbSet<TEntity> : IDbSet<TEntity>, IInternalDbSet<TEntity> where TEntity : class
{
    private readonly Func<TEntity, object?> _idAccessor;
    private readonly Func<TEntity, DocId> _docIdAccessor;
    private readonly string _idFieldName;
    private readonly Conventions _conventions;
    private readonly IMongoCollection<TEntity> _collection;

    public string CollectionName => _collection.CollectionNamespace.CollectionName;

    public DbSet(IMongoCollection<TEntity> collection, Conventions conventions)
    {
        _conventions = conventions ?? new();
        _idAccessor = EntityIdAccessor<TEntity>.GetAccessor(_conventions.IdConvention);
        _docIdAccessor = EntityIdAccessor<TEntity>.GetDocIdAccessor(_conventions.IdConvention);
        _idFieldName = _conventions.IdConvention.ResolveIdProperty<TEntity>()?.Name ?? "_id";
        _collection = collection;
    }

    public async ValueTask<TEntity?> LoadAsync(object id, CancellationToken cancellationToken = default)
    {
        var filter = Builders<TEntity>.Filter.Eq(_idFieldName, id);
        return await (await _collection.FindAsync(filter, cancellationToken: cancellationToken)).FirstOrDefaultAsync(cancellationToken);
    }

    public IDbSet<TEntity> Include(Expression<Func<TEntity, object?>> path) => this;

    public IDbSet<TEntity> Include<TInclude>(Expression<Func<TEntity, object?>> path) where TInclude : class => this;

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

    async ValueTask IInternalDbSet<TEntity>.CommitAsync(CommitContext<TEntity> context, CancellationToken cancellationToken)
    {
        context.Buffers.ModelBuffer.Clear();
        context.Buffers.UpsertBuffer.Clear();
        context.Buffers.RawIdBuffer.Clear();

        var dedupeBuffer = new ArenaSet<DocId>(context.Session.Arena, 128);

        // 1. Process Removals
        BuildDeleteModels(context.Work.Removed, context.Work.RemovedIds, ref dedupeBuffer, context.Buffers.RawIdBuffer, context.Buffers.ModelBuffer);

        // 2. Process Added (directly to models, not upsert buffer)
        // Use a temporary map to ensure "last one wins" for Added entities with the same ID
        using var addedMap = new PooledDictionary<DocId, TEntity>(16);
        foreach (var entity in context.Work.Added)
        {
            if (entity == null) continue;
            var docId = entity.GetDocId(_docIdAccessor);
            if (docId != default && !dedupeBuffer.Contains(docId))
            {
                addedMap[docId] = entity;
            }
        }

        foreach (var kvp in addedMap)
        {
            context.Buffers.ModelBuffer.Add(new InsertOneModel<TEntity>(kvp.Value));
            dedupeBuffer.Add(kvp.Key);
        }

        // 3. Process Updated/Dirty (to upsert buffer for versioning/patching)
        CollectUpdates(context.Work.Updated, context.Work.Dirty, ref dedupeBuffer, context.Buffers.UpsertBuffer);

        // 4. Apply Versions and Execute
        if (context.Buffers.UpsertBuffer.Count > 0 || context.Buffers.ModelBuffer.Count > 0)
        {
            var versionCtx = ResolveVersionContext();
            var versionMap = new ArenaDictionary<DocId, long>(context.Session.Arena, context.Buffers.UpsertBuffer.Count);
            var updateCount = 0;

            try
            {
                foreach (var entry in context.Buffers.UpsertBuffer)
                {
                    var model = PrepareUpdateOrReplaceModel(entry.Key, entry.Value.Entity, entry.Value.IsDirty, versionCtx, context.Extractor, context.Session.Tracker, ref versionMap);
                    context.Buffers.ModelBuffer.Add(model);
                    if (versionCtx.IsValid)
                    {
                        updateCount++;
                    }
                }

                if (context.Buffers.ModelBuffer.Count == 0) return;

                BulkWriteResult result = context.Session.Transaction.Session != null 
                    ? await _collection.BulkWriteAsync(context.Session.Transaction.Session, context.Buffers.ModelBuffer, cancellationToken: cancellationToken)
                    : await _collection.BulkWriteAsync(context.Buffers.ModelBuffer, cancellationToken: cancellationToken);

                if (updateCount > 0 && result.MatchedCount < updateCount)
                {
                    var failedIds = await FindConcurrencyConflictsAsync(context.Buffers.UpsertBuffer, versionMap, versionCtx.ElementName!, context.Session.Transaction.Session, cancellationToken);
                    throw new ConcurrencyException($"Optimistic concurrency check failed. Expected {updateCount} matches, but got {result.MatchedCount}.", failedIds);
                }
            }
            catch
            {
                if (versionCtx.IsValid) RevertVersions(context.Buffers.UpsertBuffer, versionMap, versionCtx.Setter!);
                throw;
            }
        }
    }

    private void BuildDeleteModels(IEnumerable<TEntity> removed, IEnumerable<object> removedIds, ref ArenaSet<DocId> dedupeBuffer, PooledHashSet<object> rawIdBuffer, PooledList<WriteModel<TEntity>> modelBuffer)
    {
        foreach (var entity in removed)
        {
            if (entity == null) continue;
            var docId = entity.GetDocId(_docIdAccessor);
            if (docId != default && dedupeBuffer.Add(docId))
            {
                rawIdBuffer.Add(entity.GetId(_idAccessor));
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

    private void CollectUpdates(IEnumerable<TEntity> updated, IEnumerable<TEntity> dirty, ref ArenaSet<DocId> dedupeBuffer, PooledDictionary<DocId, (TEntity Entity, bool IsDirty)> upsertBuffer)
    {
        foreach (var entity in updated)
        {
            if (entity == null) continue;
            var docId = entity.GetDocId(_docIdAccessor);
            if (docId != default && !dedupeBuffer.Contains(docId))
            {
                upsertBuffer[docId] = (entity, false);
            }
        }
        foreach (var entity in dirty)
        {
            if (entity == null) continue;
            var docId = entity.GetDocId(_docIdAccessor);
            if (docId != default && !dedupeBuffer.Contains(docId))
            {
                upsertBuffer[docId] = (entity, true);
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
        ref ArenaDictionary<DocId, long> versionMap)
    {
        var rawId = entity.GetId(_idAccessor);
        var filter = Builders<TEntity>.Filter.Eq(_idFieldName, rawId);

        if (versionCtx.IsValid && rawId != null)
        {
            var currentVersion = versionCtx.Getter!(entity);
            versionMap[docId] = currentVersion;

            filter = Builders<TEntity>.Filter.And(filter, Builders<TEntity>.Filter.Eq(versionCtx.ElementName!, currentVersion));
            versionCtx.Setter!(entity, currentVersion + 1);

            UpdateDefinition<TEntity>? update = null;
            if (isDirty && extractor != null && tracker != null && tracker.TryGetShadowPtr<TEntity>(docId, out var shadowPtr))
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

    private void RevertVersions(PooledDictionary<DocId, (TEntity Entity, bool IsDirty)> upsertBuffer, ArenaDictionary<DocId, long> versionMap, Action<TEntity, long> versionSetter)
    {
        foreach (var entry in upsertBuffer)
        {
            var entity = entry.Value.Entity;
            if (versionMap.TryGetValue(entry.Key, out var originalVersion))
            {
                versionSetter(entity, originalVersion);
            }
        }
    }

    private async Task<List<object>> FindConcurrencyConflictsAsync(
        PooledDictionary<DocId, (TEntity Entity, bool IsDirty)> upsertBuffer,
        ArenaDictionary<DocId, long> versionMap, 
        string concurrencyElementName, 
        IClientSessionHandle? session, 
        CancellationToken ct)
    {
        var ids = new List<object>(versionMap.Count);
        foreach (var kvp in versionMap)
        {
            if (upsertBuffer.TryGetValue(kvp.Key, out var entry))
            {
                var id = entry.Entity.GetId(_idAccessor);
                if (id != null) ids.Add(id);
            }
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

            if (versionMap.TryGetValue(docId, out var expectedVersion))
            {
                var actualVersion = BsonTypeMapper.MapToDotNetValue(doc[concurrencyElementName]);
                if (Convert.ToInt64(actualVersion) != expectedVersion)
                {
                    if (upsertBuffer.TryGetValue(docId, out var entry))
                    {
                        var id = entry.Entity.GetId(_idAccessor);
                        if (id != null) conflicts.Add(id);
                    }
                }
            }
        }

        foreach (var entry in versionMap)
        {
            if (!foundIds.Contains(entry.Key))
            {
                if (upsertBuffer.TryGetValue(entry.Key, out var upsertEntry))
                {
                    var id = upsertEntry.Entity.GetId(_idAccessor);
                    if (id != null) conflicts.Add(id);
                }
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
