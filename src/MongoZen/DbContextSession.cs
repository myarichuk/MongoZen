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
///
/// MULTI-SAVE SEMANTICS: Sessions follow the RavenDB-style "always transactional"
/// model. After each <see cref="CommitTransactionAsync"/>, a new transaction is
/// automatically started. This means a single session instance can be used for
/// multiple save cycles within its lifetime. The session is fully reset (identity
/// map + arena) after each successful save via <see cref="ClearTracking"/>.
/// </summary>
/// <typeparam name="TDbContext">The concrete DbContext type.</typeparam>
public abstract class DbContextSession<TDbContext> : IAsyncDisposable, IDbContextSession, IDbContextSessionAdvanced
    where TDbContext : DbContext
{
    private static readonly ConditionalWeakTable<IMongoClient, StrongBox<bool>> TopologyCache = new();

    // _dbContext is protected so generated subclasses can access it via GetDbContext()
    // without depending on the field name directly. Prefer GetDbContext() in generated code.
    protected readonly TDbContext _dbContext;
    private IClientSessionHandle? _session;
    private bool _ownsSession;
    private bool _inMemoryTransaction;

    private readonly ArenaAllocator _arena = new();

    // Identity Map and Shadow storage. We use object for the differ to avoid per-entity closure allocations.
    private readonly Dictionary<Type, Dictionary<object, (object Entity, IntPtr ShadowPtr, object Differ)>> _trackedEntities = new();

    /// <summary>
    /// Initialises the session.
    /// NOTE: The base constructor runs <see cref="EnsureTransactionStarted"/> before
    /// the derived constructor body executes. Derived constructors that access their
    /// own properties (e.g. MutableDbSet instances) must be aware that those are
    /// initialised in the derived body, not here.
    /// </summary>
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

    /// <summary>
    /// Starts tracking <paramref name="entity"/> in the identity map under its type
    /// and <paramref name="id"/>. If an entity with the same type+id is already tracked,
    /// the existing (canonical) instance is returned — this enforces identity-map semantics.
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public TEntity Track<TEntity>(
        TEntity entity,
        object id,
        Func<TEntity, ArenaAllocator, IntPtr> materializer,
        Func<TEntity, IntPtr, bool> differ) where TEntity : class
    {
        var type = typeof(TEntity);
        if (!_trackedEntities.TryGetValue(type, out var bucket))
        {
            bucket = new Dictionary<object, (object, IntPtr, object)>();
            _trackedEntities[type] = bucket;
        }

        if (bucket.TryGetValue(id, out var state))
        {
            return (TEntity)state.Entity;
        }

        var ptr = materializer(entity, _arena);
        bucket[id] = (entity, ptr, differ);
        return entity;
    }

    /// <summary>
    /// Returns all tracked entities of type <typeparamref name="TEntity"/> that the
    /// differ reports as dirty. O(K) where K = number of tracked entities of that type.
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public IEnumerable<TEntity> GetDirtyEntities<TEntity>() where TEntity : class
    {
        var type = typeof(TEntity);
        if (!_trackedEntities.TryGetValue(type, out var bucket))
            yield break;

        foreach (var entry in bucket)
        {
            var entity = (TEntity)entry.Value.Entity;
            var differ = (Func<TEntity, IntPtr, bool>)entry.Value.Differ;
            if (differ(entity, entry.Value.ShadowPtr))
            {
                yield return entity;
            }
        }
    }

    [EditorBrowsable(EditorBrowsableState.Never)]
    public bool TryGetEntity<TEntity>(object id, out TEntity? entity) where TEntity : class
    {
        var type = typeof(TEntity);
        if (_trackedEntities.TryGetValue(type, out var bucket) &&
            bucket.TryGetValue(id, out var entry))
        {
            entity = (TEntity)entry.Entity;
            return true;
        }

        entity = null;
        return false;
    }

    [EditorBrowsable(EditorBrowsableState.Never)]
    public void Untrack<TEntity>(object id)
    {
        var type = typeof(TEntity);
        if (_trackedEntities.TryGetValue(type, out var bucket))
        {
            bucket.Remove(id);
        }
    }

    /// <summary>
    /// Clears all tracked entities from the identity map and resets the arena allocator.
    /// Called automatically after each successful <see cref="CommitTransactionAsync"/>.
    /// Can also be called manually to discard pending change tracking.
    /// </summary>
    public void ClearTracking()
    {
        _trackedEntities.Clear();
        _arena.Reset();
    }

    [EditorBrowsable(EditorBrowsableState.Never)]
    public void TrackDynamic(object entity, Type entityType, object id)
    {
        if (!_trackedEntities.TryGetValue(entityType, out var bucket))
        {
            bucket = new Dictionary<object, (object, IntPtr, object)>();
            _trackedEntities[entityType] = bucket;
        }

        if (bucket.ContainsKey(id)) return;

        // Included entities are tracked as read-only snapshots (no shadow, never dirty).
        bucket[id] = (entity, IntPtr.Zero, (Func<object, IntPtr, bool>)((_, _) => false));
    }

    public async Task CommitTransactionAsync()
    {
        if (_inMemoryTransaction)
        {
            _inMemoryTransaction = false;
            // Auto-restart: in-memory sessions are always transactional.
            // The next EnsureTransactionStarted call will re-enter the in-memory path.
            return;
        }

        if (_session == null || !_session.IsInTransaction)
        {
            throw new InvalidOperationException("No active transaction to commit.");
        }

        await _session.CommitTransactionAsync();

        // Multi-save: immediately open the next transaction so the session stays
        // "always transactional". See class-level XML doc for details.
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

        // Fallback for standalone/unknown: synchronous round-trip with a 5-second timeout.
        // TODO: make EnsureTransactionStarted async to avoid blocking a thread-pool thread.
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
