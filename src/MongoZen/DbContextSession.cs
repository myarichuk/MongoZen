using System.Runtime.CompilerServices;
using System.ComponentModel;
using MongoDB.Driver;
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
    Task InitializeAsync(CancellationToken cancellationToken = default);
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
    protected readonly TDbContext _dbContext;
    private readonly TransactionManager _transactionManager;
    private readonly SessionArenaManager _arenaManager;
    private readonly Dictionary<Type, IInternalMutableDbSet> _dbSets = new();
    private readonly bool _startTransaction;

    // Identity Map and Shadow storage. 
    private readonly Dictionary<Type, IEntityTracker> _trackedEntities = new();

    protected DbContextSession(TDbContext dbContext, bool startTransaction = true)
    {
        _dbContext = dbContext;
        _transactionManager = new TransactionManager(dbContext);
        _arenaManager = new SessionArenaManager();
        _startTransaction = startTransaction;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_startTransaction)
        {
            await _transactionManager.EnsureTransactionStartedAsync(cancellationToken);
        }
    }

    [EditorBrowsable(EditorBrowsableState.Never)]
    public ArenaAllocator Arena => _arenaManager.Current;

    [EditorBrowsable(EditorBrowsableState.Never)]
    public IClientSessionHandle? ClientSession => _transactionManager.ClientSession;

    [EditorBrowsable(EditorBrowsableState.Never)]
    public TransactionContext Transaction => _transactionManager.TransactionContext;

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
        await _transactionManager.EnsureTransactionActiveAsync(cancellationToken);

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

        await _transactionManager.SaveChangesCommitAsync(cancellationToken);

        // AcceptChanges is only reached on the happy path — intentional.
        // If the commit above threw, shadows are NOT refreshed and the pending
        // change sets are NOT cleared, allowing the caller to retry.
        AcceptChanges();
    }

    private void AcceptChanges()
    {
        _arenaManager.IncrementGeneration();
        
        // 1. Refresh shadows for all tracked entities into _nextArena.
        foreach (var set in _dbSets.Values)
        {
            set.RefreshShadows(this);
            set.ClearTracking();
        }

        // 2. Swap arenas to preserve the newly generated shadows in _arena.
        _arenaManager.SwapAndResetNext();
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

        info.RefreshShadows(_arenaManager.Next, _arenaManager.Generation);
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

        return ((EntityTracker<TEntity>)info).Track(entity, id, forceShadow, _arenaManager.Current, _arenaManager.Generation);
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

    [EditorBrowsable(EditorBrowsableState.Never)]
    public bool TryGetShadowPtr<TEntity>(object id, out ShadowPtr shadowPtr) where TEntity : class
    {
        if (_trackedEntities.TryGetValue(typeof(TEntity), out var info) && info.TryGetShadowPtr(id, out shadowPtr))
        {
            return true;
        }
        shadowPtr = ShadowPtr.Zero;
        return false;
    }

    /// <summary>
    /// For infrastructure use only. Generated code calls this.
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public IEnumerable<TEntity> GetDirtyEntities<TEntity>() where TEntity : class
    {
        if (!_trackedEntities.TryGetValue(typeof(TEntity), out var info)) return Enumerable.Empty<TEntity>();

        return ((EntityTracker<TEntity>)info).GetDirtyEntities(_arenaManager.Generation);
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
        _arenaManager.ResetAll();
    }

    public async Task CommitTransactionAsync()
    {
        await _transactionManager.CommitTransactionAsync();
    }

    public async Task AbortTransactionAsync()
    {
        await _transactionManager.AbortTransactionAsync();
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

        await _transactionManager.DisposeAsync();
        _arenaManager.Dispose();
        GC.SuppressFinalize(this);
    }

    protected Task EnsureTransactionActiveAsync(CancellationToken cancellationToken = default) => _transactionManager.EnsureTransactionActiveAsync(cancellationToken);
}
