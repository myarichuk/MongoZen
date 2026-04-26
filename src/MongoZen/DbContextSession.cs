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

    // Identity Map and Shadow storage. 
    // Tuple: (Entity, ShadowPtr, DifferFunc, MaterializerFunc)
    private readonly Dictionary<Type, Dictionary<object, (object Entity, IntPtr ShadowPtr, object Differ, object Materializer)>> _trackedEntities = new();

    protected DbContextSession(TDbContext dbContext, bool startTransaction = true)
    {
        _dbContext = dbContext;
        if (startTransaction)
        {
            EnsureTransactionStarted();
        }
    }

    [EditorBrowsable(EditorBrowsableState.Never)]
    public ArenaAllocator Arena => _arena;

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

        var transaction = Transaction;
        foreach (var set in _dbSets.Values)
        {
            await set.CommitAsync(transaction, cancellationToken);
        }

        if (!_inMemoryTransaction && _session != null && _session.IsInTransaction)
        {
            await _session.CommitTransactionAsync(cancellationToken);
            _session.StartTransaction(); // RavenDB-style multi-save: auto-start next
        }

        AcceptChanges();
    }

    private void AcceptChanges()
    {
        // 1. Refresh shadows for all tracked entities.
        // This ensures subsequent calls to SaveChangesAsync only detect NEW changes.
        // We iterate over the registered dbSets because they know their TEntity.
        foreach (var set in _dbSets.Values)
        {
            set.RefreshShadows(this);
            set.ClearTracking();
        }
    }

    protected void RegisterDbSet<TEntity>(MutableDbSet<TEntity> set) where TEntity : class
    {
        _dbSets[typeof(TEntity)] = set;
    }

    [EditorBrowsable(EditorBrowsableState.Never)]
    public void RefreshShadows<TEntity>(Func<TEntity, ArenaAllocator, IntPtr> materializer) where TEntity : class
    {
        if (!_trackedEntities.TryGetValue(typeof(TEntity), out var map)) return;

        // Updating values in-place while iterating is safe for Dictionary in .NET.
        foreach (var kvp in map)
        {
            var entry = kvp.Value;
            if (entry.ShadowPtr == IntPtr.Zero || entry.Materializer == null) continue;

            var newShadowPtr = materializer((TEntity)entry.Entity, _arena);
            map[kvp.Key] = (entry.Entity, newShadowPtr, entry.Differ, entry.Materializer);
        }
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
            map = new Dictionary<object, (object Entity, IntPtr ShadowPtr, object Differ, object Materializer)>();
            _trackedEntities[type] = map;
        }

        if (map.TryGetValue(id, out var existing))
        {
            return (TEntity)existing.Entity;
        }

        var shadowPtr = materializer(entity, _arena);
        map[id] = (entity, shadowPtr, differ, materializer);
        return entity;
    }

    [EditorBrowsable(EditorBrowsableState.Never)]
    public void TrackDynamic(object entity, Type entityType, object id)
    {
        if (!_trackedEntities.TryGetValue(entityType, out var map))
        {
            map = new Dictionary<object, (object Entity, IntPtr ShadowPtr, object Differ, object Materializer)>();
            _trackedEntities[entityType] = map;
        }
        if (!map.ContainsKey(id))
        {
            map[id] = (entity, IntPtr.Zero, null!, null!);
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
        foreach (var set in _dbSets.Values)
        {
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
