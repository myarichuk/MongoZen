using System.Collections;
using System.Text.Json;
using MongoDB.Bson;
using MongoDB.Driver;

// ReSharper disable ComplexConditionExpression
// ReSharper disable MethodTooLong

namespace MongoZen;

public class MutableDbSet<TEntity> : IMutableDbSet<TEntity> where TEntity : class
{
    public delegate IntPtr ShadowMaterializer(TEntity entity, SharpArena.Allocators.ArenaAllocator arena);
    public delegate bool ShadowDiffer(TEntity entity, IntPtr snapshotPtr);

    private readonly IDbSet<TEntity> _baseSet;
    private readonly Func<TransactionContext>? _transactionProvider;
    private readonly ISessionTracker? _tracker;
    private readonly Func<TEntity, object?> _idAccessor;
    private readonly string _idFieldName;
    private readonly Conventions _conventions;

    private readonly ShadowMaterializer? _materializer;
    private readonly ShadowDiffer? _differ;

    private readonly List<TEntity> _added = [];
    private readonly List<TEntity> _removed = [];
    private readonly List<object> _removedIds = [];
    private readonly List<TEntity> _updated = [];

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
        ShadowMaterializer? materializer = null,
        ShadowDiffer? differ = null,
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
            _tracker?.Track(entity, id, (e, a) => _materializer(e, a), (e, p) => _differ(e, p));
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
            _tracker?.Track(entity, id, (e, a) => _materializer(e, a), (e, p) => _differ(e, p));
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

    public async ValueTask<TEntity?> LoadAsync(object id, CancellationToken cancellationToken = default)
    {
        if (_tracker != null)
        {
            var tracked = _tracker.GetDirtyEntities<TEntity>().FirstOrDefault(e => _idAccessor(e)?.Equals(id) == true);
            if (tracked != null) return tracked;
        }

        var entity = await _baseSet.LoadAsync(id, cancellationToken);
        if (entity != null && _tracker != null && _materializer != null && _differ != null)
        {
            return _tracker.Track(entity, id, (e, a) => _materializer(e, a), (e, p) => _differ(e, p));
        }
        return entity;
    }

    public IEnumerable<TEntity> GetAdded() => _added;

    public IEnumerable<TEntity> GetRemoved() => _removed;

    public IEnumerable<TEntity> GetUpdated() => _updated;

    public async Task CommitAsync(TransactionContext transaction, CancellationToken cancellationToken = default)
    {
        if (!transaction.IsActive)
        {
            throw new InvalidOperationException("A transaction is required to commit changes. Start a session with StartSession() and pass the transaction to CommitAsync().");
        }

        // Include dirty entities from tracker that aren't explicitly in _updated or _added
        var dirty = _tracker?.GetDirtyEntities<TEntity>() ?? Enumerable.Empty<TEntity>();
        var allUpdated = _updated.Concat(dirty.Where(d => !_updated.Contains(d) && !_added.Contains(d))).ToList();

        switch (_baseSet)
        {
            case InMemoryDbSet<TEntity> memSet:
                if (!transaction.IsInMemoryTransaction)
                {
                    throw new InvalidOperationException("In-memory commits require an in-memory transaction.");
                }

                await InternalCommitAsync(memSet, allUpdated, cancellationToken);
                break;
            case DbSet<TEntity> mongoSet:
                await InternalCommitAsync(mongoSet, transaction.Session, allUpdated, cancellationToken);
                break;
            default:
                throw new NotSupportedException($"The type {_baseSet.GetType()} is not supported.");
        }
    }

    public void ClearTracking()
    {
        _added.Clear();
        _removed.Clear();
        _removedIds.Clear();
        _updated.Clear();
        _tracker?.ClearTracking();
    }

    // IQueryable passthrough
    public IEnumerator<TEntity> GetEnumerator() => throw new NotSupportedException("Use QueryAsync for asynchronous execution instead of synchronous LINQ evaluation.");

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public Type ElementType => _baseSet.ElementType;

    public Expression Expression => _baseSet.Expression;

    public IQueryProvider Provider => _baseSet.Provider;

    public async ValueTask<IEnumerable<TEntity>> QueryAsync(FilterDefinition<TEntity> filter, CancellationToken cancellationToken = default)
    {
        var session = _transactionProvider?.Invoke().Session;
        var results = session != null
            ? await _baseSet.QueryAsync(filter, session, cancellationToken)
            : await _baseSet.QueryAsync(filter, cancellationToken);

        return TrackResults(results);
    }

    public async ValueTask<IEnumerable<TEntity>> QueryAsync(Expression<Func<TEntity, bool>> filter, CancellationToken cancellationToken = default)
    {
        var session = _transactionProvider?.Invoke().Session;
        var results = session != null
            ? await _baseSet.QueryAsync(filter, session, cancellationToken)
            : await _baseSet.QueryAsync(filter, cancellationToken);

        return TrackResults(results);
    }

    public async ValueTask<IEnumerable<TEntity>> QueryAsync(FilterDefinition<TEntity> filter, IClientSessionHandle session, CancellationToken cancellationToken = default)
    {
        var results = await _baseSet.QueryAsync(filter, session, cancellationToken);
        return TrackResults(results);
    }

    public async ValueTask<IEnumerable<TEntity>> QueryAsync(Expression<Func<TEntity, bool>> filter, IClientSessionHandle session, CancellationToken cancellationToken = default)
    {
        var results = await _baseSet.QueryAsync(filter, session, cancellationToken);
        return TrackResults(results);
    }

    private IEnumerable<TEntity> TrackResults(IEnumerable<TEntity> entities)
    {
        if (_tracker == null) return entities;

        return entities.Select(e =>
        {
            var id = _idAccessor(e);
            if (id != null && _materializer != null && _differ != null)
            {
                return _tracker.Track(e, id, (ent, a) => _materializer(ent, a), (ent, p) => _differ(ent, p));
            }
            return e;
        }).ToList();
    }

    private async Task InternalCommitAsync(DbSet<TEntity> mongoSet, IClientSessionHandle? session, List<TEntity> updated, CancellationToken cancellationToken = default)
    {
        var collection = mongoSet.Collection;
        var models = new List<WriteModel<TEntity>>();

        var addedByUniqueId = _added
            .Where(doc => doc is not null)
            .GroupBy(doc => doc!.GetId(_idAccessor))
            .ToDictionary(g => g.Key, g => g.Last());

        var removedIds = _removed
            .Where(e => e is not null)
            .Select(e => e!.GetId(_idAccessor))
            .Concat(_removedIds)
            .Distinct()
            .ToList();

        var updatedByUniqueId = updated
            .Where(e => e is not null)
            .GroupBy(e => e!.GetId(_idAccessor))
            .ToDictionary(g => g.Key, g => g.Last());

        if (removedIds.Count > 0)
        {
            models.Add(new DeleteManyModel<TEntity>(Builders<TEntity>.Filter.In(_idFieldName, removedIds)));
        }

        var upserts = new Dictionary<object, TEntity>();
        foreach (var entry in addedByUniqueId) upserts[entry.Key] = entry.Value!;
        foreach (var entry in updatedByUniqueId) upserts[entry.Key] = entry.Value!;

        foreach (var entry in upserts)
        {
            if (!removedIds.Contains(entry.Key))
            {
                models.Add(new ReplaceOneModel<TEntity>(Builders<TEntity>.Filter.Eq(_idFieldName, entry.Key), entry.Value) { IsUpsert = true });
            }
        }

        if (models.Count > 0)
        {
            if (session != null)
            {
                await collection.BulkWriteAsync(session, models, cancellationToken: cancellationToken);
            }
            else
            {
                await collection.BulkWriteAsync(models, cancellationToken: cancellationToken);
            }
        }
    }

    private Task InternalCommitAsync(InMemoryDbSet<TEntity> memSet, List<TEntity> updated, CancellationToken cancellationToken = default)
    {
        foreach (var entity in _added)
        {
            var existing = GetExistingFromInMemory(memSet, entity);
            if (existing != null)
            {
                memSet.Collection.Remove(existing);
            }

            memSet.Collection.Add(Clone(entity));
        }

        foreach (var entity in _removed)
        {
            var existing = GetExistingFromInMemory(memSet, entity);
            if (existing != null)
            {
                memSet.Collection.Remove(existing);
            }
        }

        foreach (var id in _removedIds)
        {
            var existing = memSet.Collection.FirstOrDefault(x => _idAccessor(x)?.Equals(id) == true);
            if (existing != null)
            {
                memSet.Collection.Remove(existing);
            }
        }

        foreach (var entity in updated)
        {
            var existing = GetExistingFromInMemory(memSet, entity);
            if (existing != null)
            {
                memSet.Collection.Remove(existing);
                memSet.Collection.Add(Clone(entity));
            }
        }

        return Task.CompletedTask;
    }

    private TEntity? GetExistingFromInMemory(InMemoryDbSet<TEntity> memSet, TEntity entity)
    {
        var id = _idAccessor(entity)
            ?? throw new InvalidOperationException("Cannot fetch entity Id without known Id property.");

        return memSet.Collection.FirstOrDefault(x =>
            x is not null
            && _idAccessor(x) is { } existingId
            && existingId.Equals(id));
    }

    private static TEntity Clone(TEntity source)
    {
        var bson = source.ToBson();
        return MongoDB.Bson.Serialization.BsonSerializer.Deserialize<TEntity>(bson);
    }
}
