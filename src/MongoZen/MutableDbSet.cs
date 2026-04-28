using System.Collections;
using System.Collections.Concurrent;
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
    private static readonly ConcurrentDictionary<(string MemberName, Type IncludeType, Type ContextType), (BsonDocument Lookup, string AsField)> _lookupCache = new();
    private IDbSet<TEntity> _dbSet;
    private readonly Func<TransactionContext>? _transactionProvider;
    private readonly ISessionTracker? _tracker;
    private readonly Func<TEntity, object?> _idAccessor;
    private readonly Func<TEntity, DocId> _docIdAccessor;
    private readonly string _idFieldName;
    private readonly Conventions _conventions;

    private readonly Func<TEntity, SharpArena.Allocators.ArenaAllocator, IntPtr>? _materializer;
    private readonly Func<TEntity, IntPtr, bool>? _differ;
    private readonly Func<TEntity, IntPtr, UpdateDefinition<TEntity>?>? _extractor;
    private readonly Func<TEntity, TEntity> _trackSingleDelegate;

    private PooledDictionary<DocId, TEntity> _added;
    private PooledList<TEntity> _removed;
    private PooledList<object> _removedIds;
    private PooledList<(LambdaExpression Path, Type IncludeType)> _includes;

    private CommitBuffers<TEntity>? _buffers;
    private PooledList<TEntity> _dirtyBuffer;

    public IMutableDbSetAdvanced<TEntity> Advanced => this;

    public void Rebind(object baseSet)
    {
        _dbSet = (IDbSet<TEntity>)baseSet;
    }

    public MutableDbSet(IDbSet<TEntity> baseSet, Conventions conventions)
    {
        _dbSet = baseSet ?? throw new ArgumentNullException(nameof(baseSet));
        _conventions = conventions ?? throw new ArgumentNullException(nameof(conventions));
        _idAccessor = EntityIdAccessor<TEntity>.GetAccessor(_conventions.IdConvention);
        _docIdAccessor = EntityIdAccessor<TEntity>.GetDocIdAccessor(_conventions.IdConvention);
        _idFieldName = _conventions.IdConvention.ResolveIdProperty<TEntity>()?.Name ?? "_id";
        _trackSingleDelegate = TrackSingle;

        _added = new PooledDictionary<DocId, TEntity>();
        _removed = new PooledList<TEntity>();
        _removedIds = new PooledList<object>();
        _includes = new PooledList<(LambdaExpression Path, Type IncludeType)>();
        _dirtyBuffer = new PooledList<TEntity>();
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

    private CommitBuffers<TEntity> Buffers => _buffers ??= new CommitBuffers<TEntity>(
        new PooledDictionary<DocId, (TEntity Entity, bool IsDirty)>(16),
        new PooledHashSet<object>(16),
        new PooledList<WriteModel<TEntity>>(16));

    public void Dispose()
    {
        _added.Dispose();
        _removed.Dispose();
        _removedIds.Dispose();
        _includes.Dispose();
        _buffers?.UpsertBuffer.Dispose();
        _buffers?.RawIdBuffer.Dispose();
        _buffers?.ModelBuffer.Dispose();
        _dirtyBuffer.Dispose();
    }

    public string CollectionName => _dbSet.CollectionName;

    public void Add(TEntity entity)
    {
        _conventions.IdGenerator.AssignId(entity, _dbSet.CollectionName, _conventions.IdConvention);
        var docId = entity.GetDocId(_docIdAccessor);
        if (docId != default)
        {
            _added[docId] = entity;
            if (_materializer != null && _differ != null)
            {
                _tracker?.Track(entity, docId, _materializer, _differ, forceShadow: false);
            }
        }
        else
        {
            // Fallback for entities where ID cannot be determined yet (rare with AssignId)
            // We use the object itself as a temporary key if we MUST, but DocId.From(entity)
            // will just return Bson hash.
            _added[DocId.From(entity)] = entity;
        }
    }

    public void Attach(TEntity entity)
    {
        var docId = entity.GetDocId(_docIdAccessor);
        if (docId == default) throw new InvalidOperationException("Cannot attach an entity without an ID.");
        if (_materializer != null && _differ != null)
        {
            _tracker?.Track(entity, docId, _materializer, _differ);
        }
    }

    public void Remove(TEntity entity)
    {
        _removed.Add(entity);
        var docId = entity.GetDocId(_docIdAccessor);
        if (docId != default)
        {
            _tracker?.Untrack<TEntity>(docId);
            _added.Remove(docId);
        }
    }

    public void Remove(object id)
    {
        _removedIds.Add(id);
        var docId = DocId.From(id);
        _tracker?.Untrack<TEntity>(docId);
        _added.Remove(docId);
    }

    public void Remove(in DocId id)
    {
        if (_tracker != null && _tracker.TryGetEntity<TEntity>(id, out var entity) && entity != null)
        {
            Remove(entity);
            return;
        }

        var rawId = id.ToBsonValue();
        if (rawId == null)
        {
             // If we have a hash (string) and it's not tracked, we can't reliably delete by DocId.
             // But we can at least ensure it's not in our tracking maps.
             _tracker?.Untrack<TEntity>(id);
             _added.Remove(id);
             throw new InvalidOperationException("Cannot remove an untracked entity by string-based DocId (hash). Use object ID instead.");
        }
        _removedIds.Add(BsonTypeMapper.MapToDotNetValue(rawId));
        _tracker?.Untrack<TEntity>(id);
        _added.Remove(id);
    }

    public void Store(TEntity entity) => Add(entity);
    public void Delete(TEntity entity) => Remove(entity);
    public void Delete(object id) => Remove(id);
    public void Delete(in DocId id) => Remove(id);

    public async ValueTask<TEntity?> LoadAsync(object id, CancellationToken cancellationToken = default)
    {
        var docId = DocId.From(id);
        if (_tracker != null && _tracker.TryGetEntity<TEntity>(docId, out var tracked) && tracked != null) return tracked;
        if (_added.TryGetValue(docId, out var added)) return added;

        var entity = await _dbSet.LoadAsync(id, cancellationToken);
        if (entity != null && _tracker != null && _materializer != null && _differ != null)
        {
            return _tracker.Track(entity, docId, _materializer, _differ);
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

    public IEnumerable<TEntity> GetAdded() => _added.Values;
    public IEnumerable<TEntity> GetRemoved() => _removed;
    public IEnumerable<TEntity> GetUpdated() => Enumerable.Empty<TEntity>();

    public void ClearTracking()
    {
        _added.Clear();
        _removed.Clear();
        _removedIds.Clear();
        _includes.Clear();

        if (_buffers != null)
        {
            _buffers.UpsertBuffer.Clear();
            _buffers.RawIdBuffer.Clear();
            _buffers.ModelBuffer.Clear();
        }
        _dirtyBuffer.Clear();
    }


    public void Reset()
    {
        _added.Dispose();
        _removed.Dispose();
        _removedIds.Dispose();
        _includes.Dispose();
        _buffers?.UpsertBuffer.Dispose();
        _buffers?.RawIdBuffer.Dispose();
        _buffers?.ModelBuffer.Dispose();
        _dirtyBuffer.Dispose();
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
            _tracker.CollectDirtyEntities(_dirtyBuffer);
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
            var work = new CommitWork<TEntity>(_added.Values, _removed, _removedIds, Enumerable.Empty<TEntity>(), _dirtyBuffer);
            var buffers = Buffers; // Reuse the class-based buffers
            var session = new SessionState(_tracker!, transaction, arena);

            var context = new CommitContext<TEntity>(work, buffers, session, _extractor);

            await ((IInternalDbSet<TEntity>)_dbSet).CommitAsync(context, cancellationToken);
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

        using var simpleMaps = new PooledList<(string AsField, Type ForeignType)>(_includes.Count);

        foreach (var (path, includeType) in _includes)
        {
            var memberExpr = path.Body as MemberExpression;
            if (memberExpr == null && path.Body is UnaryExpression unary) memberExpr = unary.Operand as MemberExpression;    
            if (memberExpr == null) continue;

            var localField = memberExpr.Member.Name;
            var foreignType = includeType;

            if (_tracker is IDbContextSession sessionTyped)
            {
                var context = sessionTyped.GetDbContext();
                var contextType = context.GetType();
                var key = (localField, foreignType, contextType);

                if (!_lookupCache.TryGetValue(key, out var cached))
                {
                    var foreignCollection = context.GetCollectionName(foreignType);
                    var asField = $"_included_{localField}";
                    var lookupDoc = new BsonDocument("$lookup", new BsonDocument 
                    { 
                        { "from", foreignCollection }, 
                        { "localField", localField }, 
                        { "foreignField", "_id" }, 
                        { "as", asField } 
                    });
                    cached = (lookupDoc, asField);
                    _lookupCache.TryAdd(key, cached);
                }

                pipeline.Add(cached.Lookup);
                simpleMaps.Add((cached.AsField, foreignType));
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
        var docId = entity.GetDocId(_docIdAccessor);
        if (docId != default && _materializer != null && _differ != null)
        {
            return _tracker.Track(entity, docId, _materializer, _differ);
        }
        return entity;
    }

    private IEnumerable<TEntity> TrackResults(IEnumerable<TEntity> entities)
    {
        if (_tracker == null) return entities;
        return entities.Select(_trackSingleDelegate);
    }
}
