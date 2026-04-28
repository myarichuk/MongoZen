using System.Runtime.CompilerServices;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Clusters;

namespace MongoZen;

internal class TransactionManager : IAsyncDisposable
{
    private static readonly ConditionalWeakTable<IMongoClient, StrongBox<bool>> TopologyCache = new();

    private readonly DbContext _dbContext;
    private IClientSessionHandle? _session;
    private bool _ownsSession;
    private bool _inMemoryTransaction;

    public TransactionManager(DbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public IClientSessionHandle? ClientSession => _session;
    public bool InMemoryTransaction => _inMemoryTransaction;
    public TransactionContext TransactionContext => new(_session, _inMemoryTransaction);

    public async Task EnsureTransactionStartedAsync(CancellationToken ct = default)
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

        if (!await TransactionsSupportedAsync(ct))
        {
            HandleUnsupportedTransactions();
            return;
        }

        if (_session == null)
        {
            _session = await _dbContext.Options.Mongo.Client.StartSessionAsync(cancellationToken: ct);
            _ownsSession = true;
        }

        _session.StartTransaction();
    }

    public async Task EnsureTransactionActiveAsync(CancellationToken ct = default)
    {
        if (!TransactionContext.IsActive)
        {
            await EnsureTransactionStartedAsync(ct);
        }
    }

    public async Task CommitTransactionAsync(CancellationToken ct = default)
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

        await _session.CommitTransactionAsync(ct);
    }

    public async Task AbortTransactionAsync(CancellationToken ct = default)
    {
        if (_inMemoryTransaction)
        {
            _inMemoryTransaction = false;
            return;
        }

        if (_session != null && _session.IsInTransaction)
        {
            await _session.AbortTransactionAsync(ct);
        }

        if (_ownsSession)
        {
            _session?.Dispose();
            _session = null;
        }
    }

    public async Task SaveChangesCommitAsync(CancellationToken ct)
    {
        if (!_inMemoryTransaction && _session != null && _session.IsInTransaction)
        {
            await _session.CommitTransactionAsync(ct);
            _session.StartTransaction();
        }
    }

    private async Task<bool> TransactionsSupportedAsync(CancellationToken ct)
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

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(5));
        
        var hello = await database.RunCommandAsync<BsonDocument>(new BsonDocument("hello", 1), cancellationToken: cts.Token);
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
    }

    public void Reset()
    {
        if (_session != null && _session.IsInTransaction)
        {
             // We can't really "reset" a MongoDB session that's in a transaction 
             // without aborting it, which is async.
             // For simplicity, if we are resetting for reuse, we assume the transaction was committed or aborted.
        }
        _inMemoryTransaction = false;
        // Keep the _session handle if we want to reuse it, but MongoDB sessions 
        // are usually short-lived. However, we'll clear it for now.
        if (_ownsSession) _session?.Dispose();
        _session = null;
        _ownsSession = false;
    }
}
