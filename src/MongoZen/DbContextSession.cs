using System.Runtime.CompilerServices;
using System.Collections.Concurrent;
using System.ComponentModel;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Clusters;
using SharpArena.Allocators;

namespace MongoZen;

/// <summary>
/// Non-generic interface for session tracking and access to the context.
/// </summary>
public interface IDbContextSession : ISessionTracker
{
    DbContext GetDbContext();
    IClientSessionHandle? ClientSession { get; }
    TransactionContext Transaction { get; }

    void Store<TEntity>(TEntity entity) where TEntity : class;
    void Delete<TEntity>(TEntity entity) where TEntity : class;
    void Delete<TEntity>(object id) where TEntity : class;

    Task SaveChangesAsync(CancellationToken cancellationToken = default);

    IDbContextSessionAdvanced Advanced { get; }
}

public interface IDbContextSessionAdvanced
{
    Task CommitTransactionAsync();
    Task AbortTransactionAsync();
    void ClearTracking();
}

/// <summary>
/// Base class for generated unit-of-work session facades.
/// Manages transaction lifecycle and MongoDB session ownership.
/// </summary>
/// <typeparam name="TDbContext">The concrete DbContext type.</typeparam>
public abstract class DbContextSession<TDbContext> : IAsyncDisposable, IDbContextSession, IDbContextSessionAdvanced
    where TDbContext : DbContext
{
    private static readonly ConditionalWeakTable<IMongoClient, StrongBox<bool>> TopologyCache = new();

    protected readonly TDbContext _dbContext;
    protected IClientSessionHandle? _session;
    protected bool _ownsSession;
    protected bool _inMemoryTransaction;

    protected readonly ArenaAllocator _arena = new();
    protected readonly Dictionary<Type, IInternalMutableDbSet> _dbSets = new();

    // Identity Map and Shadow storage. We use object for the differ to avoid per-entity closure allocations.
    private readonly Dictionary<Type, Dictionary<object, (object Entity, IntPtr ShadowPtr, object Differ)>> _trackedEntities = new();

    protected DbContextSession(TDbContext dbContext, bool startTransaction = true)
    {
        _dbContext = dbContext;
        if (startTransaction)
        {
            EnsureTransactionStarted();
        }
    }

    [EditorBrowsable(EditorBrowsableState.Never)]
    public IClientSessionHandle? ClientSession => _session;

    [EditorBrowsable(EditorBrowsableState.Never)]
    public TransactionContext Transaction => new(_session, _inMemoryTransaction);

    public DbContext GetDbContext() => _dbContext;

    public IDbContextSessionAdvanced Advanced => this;

    public virtual void Store<TEntity>(TEntity entity) where TEntity : class
        => throw new NotSupportedException("This method must be overridden by a derived class or provided by the source generator.");

    public virtual void Delete<TEntity>(TEntity entity) where TEntity : class
        => throw new NotSupportedException("This method must be overridden by a derived class or provided by the source generator.");

    public virtual void Delete<TEntity>(object id) where TEntity : class
        => throw new NotSupportedException("This method must be overridden by a derived class or provided by the source generator.");

    public virtual async Task SaveChangesAsync(CancellationToken cancellationToken = default)

    {
        EnsureTransactionActive();

        using (var commitArena = new ArenaAllocator())
        {
            foreach (var set in _dbSets.Values)
            {
                await set.CommitAsync(commitArena, _session, cancellationToken);
            }
        }

        if (!_inMemoryTransaction && _session != null && _session.IsInTransaction)
        {
            await _session.CommitTransactionAsync(cancellationToken);
            _session.StartTransaction(); // RavenDB-style multi-save: auto-start next
        }

        ClearTracking();
    }

    protected void RegisterDbSet<TEntity>(MutableDbSet<TEntity> set) where TEntity : class
    {
        _dbSets[typeof(TEntity)] = set;
    }

    [EditorBrowsable(EditorBrowsableState.Never)]
    public TEntity Track<TEntity>(
        TEntity entity,
        object id,
        Func<TEntity, ArenaAllocator, IntPtr> materializer,
        Func<TEntity, IntPtr, bool> differ) where TEntity : class
    {
        var type = typeof(TEntity);
        if (!_trackedEntities.TryGetValue(type, out var map))
        {
            map = new Dictionary<object, (object Entity, IntPtr ShadowPtr, object Differ)>();
            _trackedEntities[type] = map;
        }

        if (map.TryGetValue(id, out var existing))
        {
            return (TEntity)existing.Entity;
        }

        var shadowPtr = materializer(entity, _arena);
        map[id] = (entity, shadowPtr, differ);
        return entity;
    }

    [EditorBrowsable(EditorBrowsableState.Never)]
    public void TrackDynamic(object entity, Type entityType, object id)
    {
        // This is a simplified dynamic tracker used by Include logic.
        // Deep change tracking for dynamically-tracked included entities is TBD.
        if (!_trackedEntities.TryGetValue(entityType, out var map))
        {
            map = new Dictionary<object, (object Entity, IntPtr ShadowPtr, object Differ)>();
            _trackedEntities[entityType] = map;
        }
        if (!map.ContainsKey(id))
        {
            map[id] = (entity, IntPtr.Zero, null!);
        }
    }

    [EditorBrowsable(EditorBrowsableState.Never)]
    public void Untrack<TEntity>(object id) where TEntity : class
    {
        if (_trackedEntities.TryGetValue(typeof(TEntity), out var map))
        {
            map.Remove(id);
        }
    }

    [EditorBrowsable(EditorBrowsableState.Never)]
    public bool TryGetEntity<TEntity>(object id, out TEntity? entity) where TEntity : class
    {
        if (_trackedEntities.TryGetValue(typeof(TEntity), out var map) && map.TryGetValue(id, out var entry))
        {
            entity = (TEntity)entry.Entity;
            return true;
        }
        entity = null;
        return false;
    }

    [EditorBrowsable(EditorBrowsableState.Never)]
    public IEnumerable<TEntity> GetDirtyEntities<TEntity>() where TEntity : class
    {
        if (!_trackedEntities.TryGetValue(typeof(TEntity), out var map)) yield break;

        foreach (var entry in map.Values)
        {
            if (entry.ShadowPtr == IntPtr.Zero || entry.Differ == null) continue;
            var differ = (Func<TEntity, IntPtr, bool>)entry.Differ;
            if (differ((TEntity)entry.Entity, entry.ShadowPtr))
            {
                yield return (TEntity)entry.Entity;
            }
        }
    }

    public void ClearTracking()
    {
        _trackedEntities.Clear();
        _arena.Reset();
        // Each MutableDbSet handles its own local state (added/removed lists)
        foreach (var set in _dbSets.Values)
        {
             // We need a way to clear tracking on the set without knowing T.
             // I'll update IInternalMutableDbSet to include ClearTracking().
             set.ClearTracking();
        }
    }

    public async Task CommitTransactionAsync()
    {
        if (_inMemoryTransaction)
        {
            _inMemoryTransaction = false;
            return;
        }

        if (_session == null || !_session.IsInTransaction)
        {
            throw new InvalidOperationException("No active transaction to commit.");
        }

        await _session.CommitTransactionAsync();
        _session.StartTransaction();
    }

    public async Task AbortTransactionAsync()
    {
        if (_inMemoryTransaction)
        {
            _inMemoryTransaction = false;
            return;
        }

        if (_session != null && _session.IsInTransaction)
        {
            await _session.AbortTransactionAsync();
        }

        if (_ownsSession)
        {
            _session?.Dispose();
            _session = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_session != null)
        {
            if (_session.IsInTransaction && _ownsSession)
            {
                try { await _session.AbortTransactionAsync(); } catch { }
            }

            if (_ownsSession) _session.Dispose();
            _session = null;
        }

        _inMemoryTransaction = false;
        _arena.Dispose();
        GC.SuppressFinalize(this);
    }

    protected void EnsureTransactionActive()
    {
        if (!Transaction.IsActive)
        {
            EnsureTransactionStarted();
        }
    }

    private void EnsureTransactionStarted()
    {
        if (_inMemoryTransaction || (_session != null && _session.IsInTransaction))
        {
            return;
        }

        if (_dbContext.Options.UseInMemory)
        {
            _inMemoryTransaction = true;
            return;
        }

        if (_dbContext.Options.Mongo == null || _dbContext.Options.Conventions.DisableTransactions)
        {
            HandleUnsupportedTransactions();
            return;
        }

        if (!TransactionsSupported())
        {
            HandleUnsupportedTransactions();
            return;
        }

        if (_session == null)
        {
            _session = _dbContext.Options.Mongo.Client.StartSession();
            _ownsSession = true;
        }

        _session.StartTransaction();
    }

    private bool TransactionsSupported()
    {
        var database = _dbContext.Options.Mongo ?? throw new InvalidOperationException("Mongo not configured.");
        var client = database.Client;

        if (TopologyCache.TryGetValue(client, out var box)) return box.Value;

        if (client is MongoClient mongoClient)
        {
            var clusterType = mongoClient.Cluster.Description.Type;
            if (clusterType == ClusterType.ReplicaSet || clusterType == ClusterType.Sharded)
            {
                TopologyCache.AddOrUpdate(client, new StrongBox<bool>(true));
                return true;
            }
        }

        var hello = database.RunCommand<BsonDocument>(new BsonDocument("hello", 1), cancellationToken: new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token);
        var supported = hello.TryGetValue("setName", out _) || (hello.TryGetValue("msg", out var msg) && msg == "isdbgrid");
        TopologyCache.AddOrUpdate(client, new StrongBox<bool>(supported));
        return supported;
    }

    private void HandleUnsupportedTransactions()
    {
        if (_dbContext.Options.Conventions.TransactionSupportBehavior == TransactionSupportBehavior.Throw)
        {
            throw new InvalidOperationException("Transactions not supported.");
        }

        _inMemoryTransaction = true;
    }
}
