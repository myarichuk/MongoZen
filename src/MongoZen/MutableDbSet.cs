using System.Collections;
using System.Linq.Expressions;
using MongoDB.Bson;
using MongoDB.Driver;

// ReSharper disable ComplexConditionExpression
// ReSharper disable MethodTooLong

namespace MongoZen;

public class MutableDbSet<TEntity> : IMutableDbSet<TEntity>, IMutableDbSetAdvanced<TEntity>, IInternalMutableDbSet where TEntity : class
{
    private readonly IDbSet<TEntity> _baseSet;
    private readonly Func<TransactionContext>? _transactionProvider;
    private readonly ISessionTracker? _tracker;
    private readonly Func<TEntity, object?> _idAccessor;
    private readonly string _idFieldName;
    private readonly Conventions _conventions;

    private readonly Func<TEntity, SharpArena.Allocators.ArenaAllocator, IntPtr>? _materializer;
    private readonly Func<TEntity, IntPtr, bool>? _differ;

    // HashSet gives O(1) Contains — critical for the dirty-entity dedup in CommitAsync.
    // Reference equality is intentional: we track object identity, not value equality.
    private readonly HashSet<TEntity> _added = new(ReferenceEqualityComparer.Instance);
    private readonly List<TEntity> _removed = [];
    private readonly List<object> _removedIds = [];
    private readonly HashSet<TEntity> _updated = new(ReferenceEqualityComparer.Instance);
    private readonly List<(LambdaExpression Path, Type IncludeType)> _includes = [];

    // Pooled buffers to eliminate GC pressure during CommitAsync.
    private readonly Dictionary<object, TEntity> _commitUpsertBuffer = new();
    private readonly HashSet<object> _commitRemovedIdBuffer = new();
    private readonly List<WriteModel<TEntity>> _commitModelBuffer = new();

    public IMutableDbSetAdvanced<TEntity> Advanced => this;

    public MutableDbSet(IDbSet<TEntity> baseSet, Conventions? conventions = null)
    {
        _baseSet = baseSet;
        _conventions = conventions ?? new();
        _idAccessor = EntityIdAccessor<TEntity>.GetAccessor(_conventions.IdConvention);
        _idFieldName = _conventions.IdConvention.ResolveIdProperty<TEntity>()?.Name ?? "_id";
    }

    public MutableDbSet(
        IDbSet<TEntity> baseSet, 
        Func<TransactionContext> transactionProvider, 
        ISessionTracker tracker, 
        Func<TEntity, SharpArena.Allocators.ArenaAllocator, IntPtr>? materializer = null,
        Func<TEntity, IntPtr, bool>? differ = null,
        Conventions? conventions = null)
        : this(baseSet, conventions)
    {
        _transactionProvider = transactionProvider;
        _tracker = tracker;
        _materializer = materializer;
        _differ = differ;
    }

    public string CollectionName => _baseSet.CollectionName;

    public void Add(TEntity entity)
    {
        _conventions.IdGenerator.AssignId(entity, _baseSet.CollectionName, _conventions.IdConvention);
        _added.Add(entity);
        var id = _idAccessor(entity);
        if (id != null && _materializer != null && _differ != null)
        {
            _tracker?.Track(entity, id, _materializer, _differ);
        }
    }

    public void Attach(TEntity entity)
    {
        var id = _idAccessor(entity);
        if (id == null)
        {
            throw new InvalidOperationException("Cannot attach an entity without an ID.");
        }

        if (_materializer != null && _differ != null)
        {
            _tracker?.Track(entity, id, _materializer, _differ);
        }
    }

    public void Remove(TEntity entity)
    {
        _removed.Add(entity);
        var id = _idAccessor(entity);
        if (id != null)
        {
            _tracker?.Untrack<TEntity>(id);
        }
    }

    public void Remove(object id)
    {
        _removedIds.Add(id);
        _tracker?.Untrack<TEntity>(id);
    }

    public void Store(TEntity entity) => Add(entity);

    public void Delete(TEntity entity) => Remove(entity);

    public void Delete(object id) => Remove(id);

    public async ValueTask<TEntity?> LoadAsync(object id, CancellationToken cancellationToken = default)
    {
        if (_tracker != null && _tracker.TryGetEntity<TEntity>(id, out var tracked))
        {
            return tracked;
        }

        var entity = await _baseSet.LoadAsync(id, cancellationToken);
        if (entity != null && _tracker != null && _materializer != null && _differ != null)
        {
            return _tracker.Track(entity, id, _materializer, _differ);
        }
        return entity;
    }

    public IMutableDbSet<TEntity> Include(Expression<Func<TEntity, object?>> path)
    {
        _includes.Add((path, GetIncludeType(path)));
        return this;
    }

    public IMutableDbSet<TEntity> Include<TInclude>(Expression<Func<TEntity, object?>> path) where TInclude : class
    {
        _includes.Add((path, typeof(TInclude)));
        return this;
    }

    private static Type GetIncludeType(Expression<Func<TEntity, object?>> path)
    {
        var memberExpr = path.Body as MemberExpression;
        if (memberExpr == null && path.Body is UnaryExpression unary) memberExpr = unary.Operand as MemberExpression;
        return memberExpr?.Type ?? typeof(object);
    }

    IDbSet<TEntity> IDbSet<TEntity>.Include(Expression<Func<TEntity, object?>> path) => Include(path);
    IDbSet<TEntity> IDbSet<TEntity>.Include<TInclude>(Expression<Func<TEntity, object?>> path) where TInclude : class => Include<TInclude>(path);

    public IEnumerable<TEntity> GetAdded() => _added;

    public IEnumerable<TEntity> GetRemoved() => _removed;

    public IEnumerable<TEntity> GetUpdated() => _updated;


    async Task IInternalMutableDbSet.CommitAsync(TransactionContext transaction, CancellationToken cancellationToken)
    {
        if (!transaction.IsActive)
        {
            throw new InvalidOperationException("A transaction is required to commit changes.");
        }

        var dirty = _tracker?.GetDirtyEntities<TEntity>() ?? Enumerable.Empty<TEntity>();
        
        var allUpdated = _updated.Concat(dirty.Where(d => !_updated.Contains(d) && !_added.Contains(d))).ToList();

        if (_baseSet is IInternalDbSet<TEntity> internalSet)
        {
            await internalSet.CommitAsync(_added, _removed, _removedIds, allUpdated, _commitUpsertBuffer, _commitRemovedIdBuffer, _commitModelBuffer, transaction.Session, cancellationToken);
        }
        else
        {
            throw new NotSupportedException($"The type {_baseSet.GetType()} does not support internal commit contract.");
        }

        _added.Clear();
        _removed.Clear();
        _removedIds.Clear();
        _updated.Clear();
    }

    public void ClearTracking()
    {
        _added.Clear();
        _removed.Clear();
        _removedIds.Clear();
        _updated.Clear();
        _includes.Clear();
    }


    public async ValueTask<IEnumerable<TEntity>> QueryAsync(FilterDefinition<TEntity> filter, CancellationToken cancellationToken = default)
    {
        var session = _transactionProvider?.Invoke().Session;
        if (_includes.Count > 0 && _baseSet is DbSet<TEntity> mongoSet)
        {
            return await QueryWithIncludesAsync(mongoSet, filter, session, cancellationToken);
        }

        var results = session != null && _baseSet is DbSet<TEntity> ds
            ? await ds.QueryAsync(filter, session, cancellationToken)
            : await _baseSet.QueryAsync(filter, cancellationToken);

        return TrackResults(results);
    }

    public async ValueTask<IEnumerable<TEntity>> QueryAsync(Expression<Func<TEntity, bool>> filter, CancellationToken cancellationToken = default)
    {
        return await QueryAsync(Builders<TEntity>.Filter.Where(filter), cancellationToken);
    }

    private async Task<IEnumerable<TEntity>> QueryWithIncludesAsync(DbSet<TEntity> mongoSet, FilterDefinition<TEntity> filter, IClientSessionHandle? session, CancellationToken cancellationToken)
    {
        var pipeline = new List<BsonDocument> { new BsonDocument("$match", filter.Render(new RenderArgs<TEntity>(mongoSet.Collection.DocumentSerializer, mongoSet.Collection.Settings.SerializerRegistry))) };
        var includeMaps = new List<(string LocalField, string ForeignCollection, string AsField, Type ForeignType)>();

        foreach (var (path, includeType) in _includes)
        {
            var memberExpr = path.Body as MemberExpression;
            if (memberExpr == null && path.Body is UnaryExpression unary) memberExpr = unary.Operand as MemberExpression;
            
            if (memberExpr == null) continue;

            var localField = memberExpr.Member.Name;
            var foreignType = includeType;
            
            if (_tracker is IDbContextSession sessionTyped)
            {
                var foreignCollection = sessionTyped.GetDbContext().GetCollectionName(foreignType);
                var asField = $"_included_{localField}";
                
                pipeline.Add(new BsonDocument("$lookup", new BsonDocument
                {
                    { "from", foreignCollection },
                    { "localField", localField },
                    { "foreignField", "_id" },
                    { "as", asField }
                }));
                
                includeMaps.Add((localField, foreignCollection, asField, foreignType));
            }
            else
            {
                throw new InvalidOperationException("Includes require an IDbContextSession tracker.");
            }
        }

        List<BsonDocument> rawResults;
        if (session != null)
            rawResults = await mongoSet.Collection.Aggregate(session, PipelineDefinition<TEntity, BsonDocument>.Create(pipeline)).ToListAsync(cancellationToken);
        else
            rawResults = await mongoSet.Collection.Aggregate(PipelineDefinition<TEntity, BsonDocument>.Create(pipeline)).ToListAsync(cancellationToken);

        var entities = new List<TEntity>(rawResults.Count);
        foreach (var doc in rawResults)
        {
            foreach (var map in includeMaps)
            {
                if (doc.TryGetValue(map.AsField, out var includedArray) && includedArray.IsBsonArray)
                {
                    foreach (var includedDoc in includedArray.AsBsonArray)
                    {
                        var foreignEntity = MongoDB.Bson.Serialization.BsonSerializer.Deserialize(includedDoc.AsBsonDocument, map.ForeignType);
                        var id = includedDoc.AsBsonDocument.GetValue("_id", BsonNull.Value);
                        if (id != BsonNull.Value)
                        {
                            var idValue = BsonTypeMapper.MapToDotNetValue(id);
                            _tracker?.TrackDynamic(foreignEntity, map.ForeignType, idValue);
                        }
                    }
                    doc.Remove(map.AsField); // Clean up for deserialization of the base entity
                }
            }

            var entity = MongoDB.Bson.Serialization.BsonSerializer.Deserialize<TEntity>(doc);
            var trackedEntity = TrackSingle(entity);
            entities.Add(trackedEntity);
        }

        return entities;
    }

    private TEntity TrackSingle(TEntity entity)
    {
        if (_tracker == null) return entity;
        var id = _idAccessor(entity);
        if (id != null && _materializer != null && _differ != null)
        {
            return _tracker.Track(entity, id, _materializer, _differ);
        }
        return entity;
    }

    private IEnumerable<TEntity> TrackResults(IEnumerable<TEntity> entities)
    {
        if (_tracker == null) return entities;
        return entities.Select(TrackSingle);
    }
}
