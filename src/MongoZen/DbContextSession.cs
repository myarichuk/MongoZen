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
        try 
        {
            foreach (var set in _dbSets.Values)
            {
                await set.CommitAsync(transaction, cancellationToken);
            }
        }
        catch (ConcurrencyException)
        {
            // If we fail, we MUST NOT refresh shadows.
            // Revert version numbers in the entities because DbSet.CommitAsync already incremented them.
            RevertVersions();
            throw;
        }

        if (!_inMemoryTransaction && _session != null && _session.IsInTransaction)
        {
            // Commit first. Only auto-start the next transaction and accept
            // changes if the commit succeeds. If CommitTransactionAsync throws
            // (e.g. write conflict), the session stays in its pre-commit state
            // so the caller can inspect the error and retry or abort.
            await _session.CommitTransactionAsync(cancellationToken);
            _session.StartTransaction(); // RavenDB-style multi-save: auto-start next
        }

        // AcceptChanges is only reached on the happy path — intentional.
        // If the commit above threw, shadows are NOT refreshed and the pending
        // change sets are NOT cleared, allowing the caller to retry.
        AcceptChanges();
    }

    private void RevertVersions()
    {
        foreach (var set in _dbSets.Values)
        {
            set.RevertVersions();
        }
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
    public void RefreshShadows<TEntity>(
        Func<TEntity, SharpArena.Allocators.ArenaAllocator, IntPtr> materializer,
        Action<TEntity>? versionIncrementer = null) where TEntity : class
    {
        if (!_trackedEntities.TryGetValue(typeof(TEntity), out var map)) return;

        // NOTE: each RefreshShadows call allocates new shadow blocks from the arena
        // without reclaiming the old ones (standard arena behavior). This ensures
        // the session stays consistent but means arena memory grows on every save
        // for long-lived sessions.
        // Updating values in-place while iterating is safe for Dictionary in .NET.
        foreach (var kvp in map)
        {
            var entry = kvp.Value;
            if (entry.ShadowPtr == IntPtr.Zero || entry.Materializer == null) continue;

            var entity = (TEntity)entry.Entity;
            
            // Only increment version if we are actually refreshing (AcceptChanges)
            versionIncrementer?.Invoke(entity);

            var newShadowPtr = materializer(entity, _arena);
            map[kvp.Key] = (entity, newShadowPtr, entry.Differ, entry.Materializer);
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
        // IMPORTANT: clear the identity map BEFORE resetting the arena.
        // Any code that reads a ShadowPtr after Reset() dereferences freed memory.
        // Clearing the map first ensures no stale pointers can be reached.
        _trackedEntities.Clear();
        foreach (var set in _dbSets.Values)
        {
             set.ClearTracking();
        }
        _arena.Reset();
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

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var hello = database.RunCommand<BsonDocument>(new BsonDocument("hello", 1), cancellationToken: cts.Token);
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
