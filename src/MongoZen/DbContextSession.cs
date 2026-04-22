using System.Collections.Concurrent;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Clusters;
using SharpArena.Allocators;

namespace MongoZen;

/// <summary>
/// Base class for generated unit-of-work session facades.
/// Manages transaction lifecycle and MongoDB session ownership.
/// </summary>
/// <remarks>
/// <b>In-memory mode limitation:</b> <see cref="AbortTransactionAsync"/> for in-memory
/// mode only clears the transaction flag. It does <b>not</b> roll back any mutations
/// already committed via <see cref="MutableDbSet{TEntity}.CommitAsync"/> because
/// in-memory sets mutate their backing list directly. This is by design for testing
/// scenarios — treat in-memory commits as immediately durable.
/// </remarks>
/// <typeparam name="TDbContext">The concrete DbContext type.</typeparam>
public abstract class DbContextSession<TDbContext> : IAsyncDisposable, ISessionTracker
    where TDbContext : DbContext
{
    private static readonly ConcurrentDictionary<IMongoClient, bool> TopologyCache = new();

    protected readonly TDbContext _dbContext;
    private IClientSessionHandle? _session;
    private bool _ownsSession;
    private bool _inMemoryTransaction;
    private bool _committed;

    private readonly ArenaAllocator _arena = new();

    // Identity Map and Snapshot storage
    private readonly Dictionary<string, (object Entity, IntPtr SnapshotPtr, int Length)> _trackedEntities = new();

    protected DbContextSession(TDbContext dbContext, bool startTransaction = true)
    {
        _dbContext = dbContext;

        if (startTransaction)
        {
            StartTransaction();
        }
    }

    /// <summary>
    /// Gets the active MongoDB client session handle, if one is attached.
    /// </summary>
    public IClientSessionHandle? ClientSession => _session;

    /// <summary>
    /// Gets the current transaction context. This is cached to prevent
    /// snapshot races from creating a new struct on each access.
    /// </summary>
    public TransactionContext Transaction => new(_session, _inMemoryTransaction);

    /// <summary>
    /// Begins a transaction for this session.
    /// </summary>
    public void BeginTransaction()
    {
        if (_inMemoryTransaction)
        {
            throw new InvalidOperationException("A transaction is already active for this session.");
        }

        if (_session != null)
        {
            if (_session.IsInTransaction)
            {
                return;
            }

            if (_dbContext.Options.Mongo == null)
            {
                throw new InvalidOperationException("Mongo database not configured. This is not supposed to happen and is likely a bug.");
            }

            if (!TransactionsSupported())
            {
                HandleUnsupportedTransactions();
                return;
            }

            _session.StartTransaction();
            return;
        }

        if (_dbContext.Options.Mongo == null)
        {
            HandleUnsupportedTransactions();
            return;
        }

        _session = _dbContext.Options.Mongo.Client.StartSession();
        _ownsSession = true;
        _committed = false;
        _session.StartTransaction();
    }

    /// <summary>
    /// Attaches an externally-owned session to this DbContext session.
    /// The caller must have already started a transaction on the session.
    /// </summary>
    /// <param name="session">A session with an active transaction.</param>
    /// <exception cref="InvalidOperationException">
    /// Thrown when a session is already active or the provided session has no active transaction.
    /// </exception>
    public void UseSession(IClientSessionHandle session)
    {
        ArgumentNullException.ThrowIfNull(session);

        if (_session != null || _inMemoryTransaction)
        {
            throw new InvalidOperationException("A session is already active for this DbContext session.");
        }

        if (!session.IsInTransaction)
        {
            throw new InvalidOperationException(
                "The provided session has no active transaction. Call StartTransaction() on the session before passing it to UseSession().");
        }

        _session = session;
        _ownsSession = false;
        _committed = false;
    }

    /// <summary>
    /// Tracks an entity in the session-wide identity map.
    /// If an entity with the same ID already exists, the existing instance is returned.
    /// </summary>
    /// <typeparam name="TEntity">The type of the entity.</typeparam>
    /// <param name="entity">The entity instance to track.</param>
    /// <param name="id">The ID of the entity.</param>
    /// <returns>The tracked entity instance.</returns>
    public TEntity Track<TEntity>(TEntity entity, object id) where TEntity : class
    {
        var key = GetEntityKey<TEntity>(id);
        if (_trackedEntities.TryGetValue(key, out var state))
        {
            return (TEntity)state.Entity;
        }

        var bson = entity.ToBson();
        unsafe
        {
            var ptr = _arena.Alloc((nuint)bson.Length);
            var dest = new Span<byte>(ptr, bson.Length);
            bson.CopyTo(dest);
            _trackedEntities[key] = (entity, (IntPtr)ptr, bson.Length);
        }

        return entity;
    }

    /// <summary>
    /// Gets all tracked entities that have changed since they were last tracked.
    /// </summary>
    /// <typeparam name="TEntity">The type of the entities to check.</typeparam>
    /// <returns>A collection of changed entities.</returns>
    public IEnumerable<TEntity> GetDirtyEntities<TEntity>() where TEntity : class
    {
        var typeName = typeof(TEntity).Name;
        foreach (var state in _trackedEntities)
        {
            if (state.Key.StartsWith(typeName + "/") && state.Value.Entity is TEntity entity)
            {
                bool isDirty;
                var currentBson = entity.ToBson();
                unsafe
                {
                    var snapshotSpan = new ReadOnlySpan<byte>((void*)state.Value.SnapshotPtr, state.Value.Length);
                    isDirty = !snapshotSpan.SequenceEqual(currentBson);
                }

                if (isDirty)
                {
                    yield return entity;
                }
            }
        }
    }

    /// <summary>
    /// Clears tracking for a specific entity.
    /// </summary>
    /// <typeparam name="TEntity">The type of the entity.</typeparam>
    /// <param name="id">The ID of the entity.</param>
    public void Untrack<TEntity>(object id)
    {
        var key = GetEntityKey<TEntity>(id);
        _trackedEntities.Remove(key);
    }

    /// <summary>
    /// Clears all tracking in the session.
    /// </summary>
    public void ClearTracking()
    {
        _trackedEntities.Clear();
        _arena.Reset();
    }

    private string GetEntityKey<TEntity>(object id)
    {
        return $"{typeof(TEntity).Name}/{id}";
    }

    /// <summary>
    /// Commits the active transaction.
    /// </summary>
    /// <returns>A task that completes when the commit operation has finished.</returns>
    public async Task CommitTransactionAsync()
    {
        if (_inMemoryTransaction)
        {
            _inMemoryTransaction = false;
            _committed = true;
            return;
        }

        if (_session == null)
        {
            throw new InvalidOperationException("No active transaction to commit.");
        }

        if (!_session.IsInTransaction)
        {
            throw new InvalidOperationException("The active session has no transaction to commit.");
        }

        await _session.CommitTransactionAsync();
        _committed = true;
        if (_ownsSession)
        {
            _session.Dispose();
            _session = null;
        }
    }

    /// <summary>
    /// Aborts the active transaction.
    /// </summary>
    /// <returns>A task that completes when the abort operation has finished.</returns>
    public async Task AbortTransactionAsync()
    {
        if (_inMemoryTransaction)
        {
            _inMemoryTransaction = false;
            return;
        }

        if (_session == null)
        {
            throw new InvalidOperationException("No active transaction to abort.");
        }

        if (_session.IsInTransaction)
        {
            await _session.AbortTransactionAsync();
        }

        if (_ownsSession)
        {
            _session.Dispose();
            _session = null;
        }
    }

    /// <summary>
    /// Disposes the session and aborts any active transaction to prevent resource leaks.
    /// </summary>
    /// <returns>A <see cref="ValueTask"/> representing the asynchronous dispose operation.</returns>
    public async ValueTask DisposeAsync()
    {
        if (_session != null)
        {
            if (_session.IsInTransaction && _ownsSession)
            {
                try
                {
                    await _session.AbortTransactionAsync();
                }
                catch
                {
                    // Best-effort abort during disposal — don't let exceptions escape.
                }
            }

            if (_ownsSession)
            {
                _session.Dispose();
            }

            _session = null;
        }

        _inMemoryTransaction = false;
        _arena.Dispose();
        GC.SuppressFinalize(this);
    }

    protected void EnsureTransactionActive()
    {
        if (_committed)
        {
            throw new InvalidOperationException(
                "This session has already committed. Call BeginTransaction() to start a new transaction before calling SaveChangesAsync() again.");
        }

        if (!Transaction.IsActive)
        {
            throw new InvalidOperationException("A transaction is required to save changes. Start a session with StartSession() first.");
        }

        if (_session != null && !_session.IsInTransaction)
        {
            if (_dbContext.Options.Conventions.TransactionSupportBehavior == TransactionSupportBehavior.Throw)
            {
                throw new InvalidOperationException("MongoDB commits require an active transaction.");
            }

            _inMemoryTransaction = true;
        }
    }

    private void StartTransaction()
    {
        if (_dbContext.Options.UseInMemory)
        {
            _inMemoryTransaction = true;
            return;
        }

        if (_dbContext.Options.Mongo == null)
        {
            throw new InvalidOperationException("Mongo database not configured. This is not supposed to happen and is likely a bug.");
        }

        if (!TransactionsSupported())
        {
            HandleUnsupportedTransactions();
            return;
        }

        if (_session == null)
        {
            if (_dbContext.Options.Mongo == null)
            {
                HandleUnsupportedTransactions();
                return;
            }

            _session = _dbContext.Options.Mongo.Client.StartSession();
            _ownsSession = true;
        }

        if (!_session.IsInTransaction)
        {
            _session.StartTransaction();
        }
    }

    private bool TransactionsSupported()
    {
        var database = _dbContext.Options.Mongo ?? throw new InvalidOperationException("Mongo database not configured. This is not supposed to happen and is likely a bug.");
        var client = database.Client;

        if (TopologyCache.TryGetValue(client, out var supported))
        {
            return supported;
        }

        if (client is MongoClient mongoClient)
        {
            var clusterType = mongoClient.Cluster.Description.Type;
            if (clusterType == ClusterType.ReplicaSet || clusterType == ClusterType.Sharded)
            {
                TopologyCache.TryAdd(client, true);
                return true;
            }
        }

        var hello = database.RunCommand<BsonDocument>(new BsonDocument("hello", 1));
        var isSharded = hello.TryGetValue("msg", out var msg) && msg == "isdbgrid";
        var isReplicaSet = hello.TryGetValue("setName", out _);

        supported = isReplicaSet || isSharded;
        TopologyCache.TryAdd(client, supported);
        return supported;
    }

    private void HandleUnsupportedTransactions()
    {
        if (_dbContext.Options.Conventions.TransactionSupportBehavior == TransactionSupportBehavior.Throw)
        {
            throw new InvalidOperationException(
                "MongoDB transactions require a replica set or sharded cluster. Configure Conventions.TransactionSupportBehavior to Simulate to fall back to a non-transactional unit of work.");
        }

        _inMemoryTransaction = true;
    }
}
