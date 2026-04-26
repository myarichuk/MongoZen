using System.Runtime.CompilerServices;
using System.Collections.Concurrent;
using System.ComponentModel;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Clusters;
using SharpArena.Allocators;

namespace MongoZen;

internal interface IEntityTracker : IDisposable
{
    void RefreshShadows(ArenaAllocator arena, int generation);
    void TrackDynamic(object entity, object id);
    bool TryGetEntity(object id, out object? entity);
    void Untrack(object id);
}

internal class EntityTracker<TEntity> : IEntityTracker where TEntity : class
{
    private readonly Func<TEntity, IntPtr, bool>? _differ;
    private readonly Func<TEntity, ArenaAllocator, IntPtr>? _materializer;
    public MongoZen.Collections.PooledDictionary<object, (TEntity Entity, ShadowPtr ShadowPtr)> Map;

    public EntityTracker()
    {
        Map = new MongoZen.Collections.PooledDictionary<object, (TEntity Entity, ShadowPtr ShadowPtr)>(16);
    }

    public EntityTracker(Func<TEntity, IntPtr, bool>? differ, Func<TEntity, ArenaAllocator, IntPtr>? materializer)
        : this()
    {
        _differ = differ;
        _materializer = materializer;
    }

    public void RefreshShadows(ArenaAllocator arena, int generation)
    {
        if (_materializer == null) return;

        foreach (var kvp in Map)
        {
            var entry = kvp.Value;
            var newShadowPtr = new ShadowPtr(_materializer(entry.Entity, arena), generation);
            Map[kvp.Key] = (entry.Entity, newShadowPtr);
        }
    }

    public void TrackDynamic(object entity, object id)
    {
        if (!Map.ContainsKey(id))
        {
            Map[id] = ((TEntity)entity, ShadowPtr.Zero);
        }
    }

    public bool TryGetEntity(object id, out object? entity)
    {
        if (Map.TryGetValue(id, out var entry))
        {
            entity = entry.Entity;
            return true;
        }
        entity = null;
        return false;
    }

    public void Untrack(object id) => Map.Remove(id);

    public IEnumerable<TEntity> GetDirtyEntities(int currentGeneration)
    {
        if (_differ == null) yield break;

        foreach (var kvp in Map)
        {
            var entry = kvp.Value;
            if (entry.ShadowPtr.IsZero) continue;
#if DEBUG
            if (entry.ShadowPtr.Generation != currentGeneration)
            {
                throw new InvalidOperationException("Attempted to access a shadow pointer from a previous arena generation. This pointer is stale and unsafe to use.");
            }
#endif
            if (_differ(entry.Entity, entry.ShadowPtr))
            {
                yield return entry.Entity;
            }
        }
    }

    public TEntity Track(TEntity entity, object id, bool forceShadow, ArenaAllocator arena, int generation)
    {
        if (Map.TryGetValue(id, out var existing))
        {
            return existing.Entity;
        }

        ShadowPtr shadowPtr = ShadowPtr.Zero;
        if (forceShadow)
        {
            shadowPtr = new ShadowPtr(_materializer(entity, arena), generation);
        }

        Map[id] = (entity, shadowPtr);
        return entity;
    }

    public void Dispose() => Map.Dispose();
}

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
    protected int _arenaGeneration = 0;
    protected readonly Dictionary<Type, IInternalMutableDbSet> _dbSets = new();

    // Identity Map and Shadow storage. 
    private readonly Dictionary<Type, IEntityTracker> _trackedEntities = new();

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

    /// <summary>
    /// For infrastructure use only. Generated code calls this.
    /// </summary>
    public virtual void Store<TEntity>(TEntity entity) where TEntity : class
        => throw new NotSupportedException("This method must be overridden by a derived class or provided by the source generator.");

    /// <summary>
    /// For infrastructure use only. Generated code calls this.
    /// </summary>
    public virtual void Delete<TEntity>(TEntity entity) where TEntity : class
        => throw new NotSupportedException("This method must be overridden by a derived class or provided by the source generator.");

    /// <summary>
    /// For infrastructure use only. Generated code calls this.
    /// </summary>
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
            // Version numbers are reverted in DbSet.CommitAsync locally.
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

    /// <summary>
    /// For infrastructure use only. Generated code calls this.
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public void RefreshShadows<TEntity>(
        Func<TEntity, SharpArena.Allocators.ArenaAllocator, IntPtr> materializer,
        Action<TEntity>? versionIncrementer = null) where TEntity : class
    {
        if (!_trackedEntities.TryGetValue(typeof(TEntity), out var info)) return;

        info.RefreshShadows(_arena, _arenaGeneration);
    }

    /// <summary>
    /// For infrastructure use only. Generated code calls this.
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public TEntity Track<TEntity>(
        TEntity entity,
        object id,
        Func<TEntity, ArenaAllocator, IntPtr> materializer,
        Func<TEntity, IntPtr, bool> differ) where TEntity : class
        => Track(entity, id, materializer, differ, true);

    /// <summary>
    /// For infrastructure use only. Generated code calls this.
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public TEntity Track<TEntity>(
        TEntity entity,
        object id,
        Func<TEntity, ArenaAllocator, IntPtr> materializer,
        Func<TEntity, IntPtr, bool> differ,
        bool forceShadow) where TEntity : class
    {
        var type = typeof(TEntity);
        if (!_trackedEntities.TryGetValue(type, out var info))
        {
            info = new EntityTracker<TEntity>(differ, materializer);
            _trackedEntities[type] = info;
        }

        return ((EntityTracker<TEntity>)info).Track(entity, id, forceShadow, _arena, _arenaGeneration);
    }


    /// <summary>
    /// For infrastructure use only. Generated code calls this.
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public void TrackDynamic(object entity, Type entityType, object id)
    {
        if (!_trackedEntities.TryGetValue(entityType, out var info))
        {
            // Fallback for types not known at compile time - limited tracking
            // Since we don't have differ/materializer, we can't do full tracking
            // but we can at least put it in the identity map.
            // This is used for Includes.
            var trackerType = typeof(EntityTracker<>).MakeGenericType(entityType);
            info = (IEntityTracker)Activator.CreateInstance(trackerType, null!, null!)!;
            _trackedEntities[entityType] = info;
        }
        info.TrackDynamic(entity, id);
    }

    /// <summary>
    /// For infrastructure use only. Generated code calls this.
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public void Untrack<TEntity>(object id) where TEntity : class
    {
        if (_trackedEntities.TryGetValue(typeof(TEntity), out var info))
        {
            info.Untrack(id);
        }
    }

    /// <summary>
    /// For infrastructure use only. Generated code calls this.
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public bool TryGetEntity<TEntity>(object id, out TEntity? entity) where TEntity : class
    {
        if (_trackedEntities.TryGetValue(typeof(TEntity), out var info) && info.TryGetEntity(id, out var obj))
        {
            entity = (TEntity?)obj;
            return true;
        }
        entity = null;
        return false;
    }

    /// <summary>
    /// For infrastructure use only. Generated code calls this.
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public IEnumerable<TEntity> GetDirtyEntities<TEntity>() where TEntity : class
    {
        if (!_trackedEntities.TryGetValue(typeof(TEntity), out var info)) return Enumerable.Empty<TEntity>();

        return ((EntityTracker<TEntity>)info).GetDirtyEntities(_arenaGeneration);
    }


    public void ClearTracking()
    {
        // IMPORTANT: clear the identity map BEFORE resetting the arena.
        // Any code that reads a ShadowPtr after Reset() dereferences freed memory.
        // Clearing the map first ensures no stale pointers can be reached.
        foreach (var info in _trackedEntities.Values)
        {
            info.Dispose();
        }
        _trackedEntities.Clear();

        foreach (var set in _dbSets.Values)
        {
             set.ClearTracking();
        }
        _arena.Reset();
        _arenaGeneration++;
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
        foreach (var info in _trackedEntities.Values)
        {
            info.Dispose();
        }
        _trackedEntities.Clear();

        foreach (var set in _dbSets.Values)
        {
            set.Dispose();
        }
        _dbSets.Clear();

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
