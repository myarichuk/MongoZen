using System.Collections;
using System.Collections.Generic;
using System.Linq.Expressions;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoZen.Collections;

// ReSharper disable ComplexConditionExpression
// ReSharper disable MethodTooLong

namespace MongoZen;

public class MutableDbSet<TEntity> : IMutableDbSet<TEntity>, IMutableDbSetAdvanced<TEntity>, IInternalMutableDbSet where TEntity : class
{
    private readonly IDbSet<TEntity> _dbSet;
    private readonly Func<TransactionContext>? _transactionProvider;
    private readonly ISessionTracker? _tracker;
    private readonly Func<TEntity, object?> _idAccessor;
    private readonly string _idFieldName;
    private readonly Conventions _conventions;

    private readonly Func<TEntity, SharpArena.Allocators.ArenaAllocator, IntPtr>? _materializer;
    private readonly Func<TEntity, IntPtr, bool>? _differ;
    private readonly Func<TEntity, IntPtr, UpdateDefinition<TEntity>?>? _extractor;
    private readonly Func<TEntity, TEntity> _trackSingleDelegate;

    private PooledHashSet<TEntity> _added;
    private PooledList<TEntity> _removed;
    private PooledList<object> _removedIds;
    private PooledHashSet<TEntity> _updated;
    private PooledList<(LambdaExpression Path, Type IncludeType)> _includes;

    private PooledDictionary<DocId, (TEntity Entity, bool IsDirty)> _upsertBuffer;
    private PooledHashSet<object> _rawIdBuffer;
    private PooledList<WriteModel<TEntity>> _modelBuffer;
    private PooledList<TEntity> _dirtyBuffer;

    public IMutableDbSetAdvanced<TEntity> Advanced => this;

    public MutableDbSet(IDbSet<TEntity> baseSet, Conventions conventions)
    {
        _dbSet = baseSet ?? throw new ArgumentNullException(nameof(baseSet));
        _conventions = conventions ?? throw new ArgumentNullException(nameof(conventions));
        _idAccessor = EntityIdAccessor<TEntity>.GetAccessor(_conventions.IdConvention);
        _idFieldName = _conventions.IdConvention.ResolveIdProperty<TEntity>()?.Name ?? "_id";
        _trackSingleDelegate = TrackSingle;
        
        // Initialize pooled collections with reference equality where needed
        _added = new PooledHashSet<TEntity>(16, ReferenceEqualityComparer.Instance);
        _removed = new PooledList<TEntity>(8);
        _removedIds = new PooledList<object>(8);
        _updated = new PooledHashSet<TEntity>(16, ReferenceEqualityComparer.Instance);
        _includes = new PooledList<(LambdaExpression Path, Type IncludeType)>(4);

        _upsertBuffer = new PooledDictionary<DocId, (TEntity Entity, bool IsDirty)>(16);
        _rawIdBuffer = new PooledHashSet<object>(16);
        _modelBuffer = new PooledList<WriteModel<TEntity>>(16);
        _dirtyBuffer = new PooledList<TEntity>(16);
    }

    public MutableDbSet(
        IDbSet<TEntity> baseSet, 
        Func<TransactionContext> transactionProvider, 
        ISessionTracker tracker, 
        Func<TEntity, SharpArena.Allocators.ArenaAllocator, IntPtr>? materializer = null,
        Func<TEntity, IntPtr, bool>? differ = null,
        Func<TEntity, IntPtr, UpdateDefinition<TEntity>?>? extractor = null,
        Conventions? conventions = null)
        : this(baseSet, conventions ?? new Conventions())
    {
        _transactionProvider = transactionProvider;
        _tracker = tracker;
        _materializer = materializer;
        _differ = differ;
        _extractor = extractor;
    }

    public void Dispose()
    {
        _added.Dispose();
        _removed.Dispose();
        _removedIds.Dispose();
        _updated.Dispose();
        _includes.Dispose();
        _upsertBuffer.Dispose();
        _rawIdBuffer.Dispose();
        _modelBuffer.Dispose();
        _dirtyBuffer.Dispose();
    }

    public string CollectionName => _dbSet.CollectionName;

    public void Add(TEntity entity)
    {
        _conventions.IdGenerator.AssignId(entity, _dbSet.CollectionName, _conventions.IdConvention);
        _added.Add(entity);
        var id = _idAccessor(entity);
        if (id != null && _materializer != null && _differ != null)
        {
            _tracker?.Track(entity, id, _materializer, _differ, forceShadow: false);
        }
    }

    public void Attach(TEntity entity)
    {
        var id = _idAccessor(entity);
        if (id == null) throw new InvalidOperationException("Cannot attach an entity without an ID.");
        if (_materializer != null && _differ != null)
        {
            _tracker?.Track(entity, id, _materializer, _differ);
        }
    }

    public void Remove(TEntity entity)
    {
        _removed.Add(entity);
        var id = _idAccessor(entity);
        if (id != null) _tracker?.Untrack<TEntity>(id);
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
        if (_tracker != null && _tracker.TryGetEntity<TEntity>(id, out var tracked)) return tracked;

        var entity = await _dbSet.LoadAsync(id, cancellationToken);
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
        return memberExpr?.Type ?? throw new ArgumentException("Could not resolve include type from expression. Ensure it is a simple property access.");
    }

    IDbSet<TEntity> IDbSet<TEntity>.Include(Expression<Func<TEntity, object?>> path) => Include(path);
    IDbSet<TEntity> IDbSet<TEntity>.Include<TInclude>(Expression<Func<TEntity, object?>> path) where TInclude : class => Include<TInclude>(path);

    public IEnumerable<TEntity> GetAdded() => _added;
    public IEnumerable<TEntity> GetRemoved() => _removed;
    public IEnumerable<TEntity> GetUpdated() => _updated;

    public void ClearTracking()
    {
        _added.Dispose();
        _removed.Dispose();
        _removedIds.Dispose();
        _updated.Dispose();
        _includes.Dispose();

        _upsertBuffer.Dispose();
        _rawIdBuffer.Dispose();
        _modelBuffer.Dispose();
        _dirtyBuffer.Dispose();

        // Re-initialize for next use
        _added = new PooledHashSet<TEntity>(16, ReferenceEqualityComparer.Instance);
        _removed = new PooledList<TEntity>(8);
        _removedIds = new PooledList<object>(8);
        _updated = new PooledHashSet<TEntity>(16, ReferenceEqualityComparer.Instance);
        _includes = new PooledList<(LambdaExpression Path, Type IncludeType)>(4);

        _upsertBuffer = new PooledDictionary<DocId, (TEntity Entity, bool IsDirty)>(16);
        _rawIdBuffer = new PooledHashSet<object>(16);
        _modelBuffer = new PooledList<WriteModel<TEntity>>(16);
        _dirtyBuffer = new PooledList<TEntity>(16);
    }


    void IInternalMutableDbSet.RefreshShadows(ISessionTracker tracker)
    {
        if (_materializer != null)
        {
            tracker.RefreshShadows<TEntity>(_materializer);
        }
    }

    async ValueTask IInternalMutableDbSet.CommitAsync(TransactionContext transaction, CancellationToken cancellationToken)
    {
        _dirtyBuffer.Clear();
        if (_tracker != null)
        {
            foreach (var d in _tracker.GetDirtyEntities<TEntity>())
            {
                if (!_updated.Contains(d) && !_added.Contains(d))
                {
                    _dirtyBuffer.Add(d);
                }
            }
        }

        var arena = _tracker?.Arena;
        bool ownsArena = false;
        if (arena == null)
        {
            arena = new SharpArena.Allocators.ArenaAllocator();
            ownsArena = true;
        }

        try
        {
            await ((IInternalDbSet<TEntity>)_dbSet).CommitAsync(
                _added, 
                _removed, 
                _removedIds, 
                _updated, 
                _dirtyBuffer,
                _upsertBuffer,
                _rawIdBuffer,
                _modelBuffer,
                _extractor,
                _tracker!,
                transaction, arena, cancellationToken);
        }
        finally
        {
            if (ownsArena) arena.Dispose();
        }
    }



    public async ValueTask<IEnumerable<TEntity>> QueryAsync(FilterDefinition<TEntity> filter, CancellationToken cancellationToken = default)
    {
        var session = _transactionProvider?.Invoke().Session;
        if (_includes.Count > 0)
        {
            if (_dbSet is DbSet<TEntity> mongoSet)
            {
                return await QueryWithIncludesAsync(mongoSet, filter, session, cancellationToken);
            }

            throw new InvalidOperationException(
                $"Includes are only supported on the default MongoDB DbSet implementation. " +
                $"The current implementation '{_dbSet.GetType().Name}' does not support Includes.");
        }

        var results = session != null && _dbSet is DbSet<TEntity> ds
            ? await ds.QueryAsync(filter, session, cancellationToken)
            : await _dbSet.QueryAsync(filter, cancellationToken);

        return TrackResults(results);
    }


    public async ValueTask<IEnumerable<TEntity>> QueryAsync(Expression<Func<TEntity, bool>> filter, CancellationToken cancellationToken = default)
    {
        return await QueryAsync(Builders<TEntity>.Filter.Where(filter), cancellationToken);
    }

    private async Task<IEnumerable<TEntity>> QueryWithIncludesAsync(DbSet<TEntity> mongoSet, FilterDefinition<TEntity> filter, IClientSessionHandle? session, CancellationToken cancellationToken)
    {
        using var pipeline = new PooledList<BsonDocument>(16);
        pipeline.Add(new BsonDocument("$match", filter.Render(new RenderArgs<TEntity>(mongoSet.Collection.DocumentSerializer, mongoSet.Collection.Settings.SerializerRegistry))));

        using var includeMaps = new PooledList<(string LocalField, string ForeignCollection, string AsField, Type ForeignType)>(8);

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
                pipeline.Add(new BsonDocument("$lookup", new BsonDocument { { "from", foreignCollection }, { "localField", localField }, { "foreignField", "_id" }, { "as", asField } }));
                includeMaps.Add((localField, foreignCollection, asField, foreignType));
            }
            else
            {
                throw new InvalidOperationException(
                    $"Includes on '{typeof(TEntity).Name}' require an IDbContextSession tracker. " +
                    "Make sure the MutableDbSet was created with a session (not standalone) and the " +
                    "underlying IDbSet<T> is a DbSet<T> backed by a real MongoDB collection.");
            }
        }

        // Use the new IncludeWrappingSerializer to reduce GC pressure and avoid full AST parsing
        var innerSerializer = mongoSet.Collection.DocumentSerializer;
        var simpleMaps = new List<(string AsField, Type ForeignType)>(includeMaps.Count);
        foreach (var map in includeMaps) simpleMaps.Add((map.AsField, map.ForeignType));
        
        var wrappingSerializer = new IncludeWrappingSerializer<TEntity>(innerSerializer, _tracker!, simpleMaps);

        var pipelineDefinition = PipelineDefinition<TEntity, TEntity>.Create(pipeline, wrappingSerializer);

        List<TEntity> results;
        if (session != null)
            results = await mongoSet.Collection.Aggregate(session, pipelineDefinition).ToListAsync(cancellationToken);
        else
            results = await mongoSet.Collection.Aggregate(pipelineDefinition).ToListAsync(cancellationToken);

        return results.Select(TrackSingle);
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
        return entities.Select(_trackSingleDelegate);
    }
}
