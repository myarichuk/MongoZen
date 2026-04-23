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
/// </summary>
/// <typeparam name="TDbContext">The concrete DbContext type.</typeparam>
public abstract class DbContextSession<TDbContext> : IAsyncDisposable, IDbContextSession, IDbContextSessionAdvanced
    where TDbContext : DbContext
{
    private static readonly ConcurrentDictionary<IMongoClient, bool> TopologyCache = new();

    protected readonly TDbContext _dbContext;
    private IClientSessionHandle? _session;
    private bool _ownsSession;
    private bool _inMemoryTransaction;
    private bool _committed;

    private readonly ArenaAllocator _arena = new();

    // Identity Map and Shadow storage
    private readonly Dictionary<string, (object Entity, IntPtr ShadowPtr, Func<object, IntPtr, bool> Differ)> _trackedEntities = new();

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

    [EditorBrowsable(EditorBrowsableState.Never)]
    public TEntity Track<TEntity>(
        TEntity entity, 
        object id, 
        Func<TEntity, ArenaAllocator, IntPtr> materializer, 
        Func<TEntity, IntPtr, bool> differ) where TEntity : class
    {
        var key = GetEntityKey<TEntity>(id);
        if (_trackedEntities.TryGetValue(key, out var state))
        {
            return (TEntity)state.Entity;
        }

        var ptr = materializer(entity, _arena);
        _trackedEntities[key] = (entity, ptr, (e, p) => differ((TEntity)e, p));
        return entity;
    }

    [EditorBrowsable(EditorBrowsableState.Never)]
    public IEnumerable<TEntity> GetDirtyEntities<TEntity>() where TEntity : class
    {
        var typeName = typeof(TEntity).Name;
        var prefix = typeName + "/";
        foreach (var entry in _trackedEntities)
        {
            if (entry.Key.StartsWith(prefix) && entry.Value.Entity is TEntity entity)
            {
                if (entry.Value.Differ(entity, entry.Value.ShadowPtr))
                {
                    yield return entity;
                }
            }
        }
    }

    [EditorBrowsable(EditorBrowsableState.Never)]
    public void Untrack<TEntity>(object id)
    {
        var key = GetEntityKey<TEntity>(id);
        _trackedEntities.Remove(key);
    }

    public void ClearTracking()
    {
        _trackedEntities.Clear();
        _arena.Reset();
    }

    [EditorBrowsable(EditorBrowsableState.Never)]
    public void TrackDynamic(object entity, Type entityType, object id)
    {
        var key = $"{entityType.Name}/{id}";
        if (_trackedEntities.ContainsKey(key)) return;

        // Included entities are tracked as read-only snapshots for now.
        // We don't have a differ for them unless we use reflection or source gen.
        _trackedEntities[key] = (entity, IntPtr.Zero, (_, _) => false);
    }

    private string GetEntityKey<TEntity>(object id)
    {
        return $"{typeof(TEntity).Name}/{id}";
    }

    public async Task CommitTransactionAsync()
    {
        if (_inMemoryTransaction)
        {
            _inMemoryTransaction = false;
            _committed = true;
            return;
        }

        if (_session == null || !_session.IsInTransaction)
        {
            throw new InvalidOperationException("No active transaction to commit.");
        }

        await _session.CommitTransactionAsync();
        _committed = true;
        if (_ownsSession)
        {
            _session.Dispose();
            _session = null;
        }
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
        if (_committed)
        {
            throw new InvalidOperationException("This session has already committed.");
        }

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
        _committed = false;
    }

    private bool TransactionsSupported()
    {
        var database = _dbContext.Options.Mongo ?? throw new InvalidOperationException("Mongo not configured.");
        var client = database.Client;

        if (TopologyCache.TryGetValue(client, out var supported)) return supported;

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
        supported = hello.TryGetValue("setName", out _) || (hello.TryGetValue("msg", out var msg) && msg == "isdbgrid");
        TopologyCache.TryAdd(client, supported);
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
