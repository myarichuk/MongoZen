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

    public ArenaAllocator Arena => _arena;

    public IClientSessionHandle? ClientSession => _session;

    public TransactionContext Transaction => new(_session, _inMemoryTransaction);

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

        if (_dbContext.Options.Mongo == null)
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
