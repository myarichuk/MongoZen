using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Clusters;

namespace MongoZen;

public abstract class DbContextSession<TDbContext> : IDisposable, IAsyncDisposable
    where TDbContext : DbContext
{
    protected readonly TDbContext _dbContext;
    private IClientSessionHandle? _session;
    private bool _ownsSession;
    private bool _inMemoryTransaction;
    private bool _disposed;
    private bool? _transactionsSupported;

    protected DbContextSession(TDbContext dbContext, bool startTransaction = true)
    {
        _dbContext = dbContext;

        if (startTransaction)
        {
            StartTransaction();
        }
    }

    public IClientSessionHandle? ClientSession => _session;

    public TransactionContext Transaction => new(_session, _inMemoryTransaction);

    public void BeginTransaction()
    {
        if (_inMemoryTransaction)
        {
            return;
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

        StartTransaction();
    }

    public void UseSession(IClientSessionHandle session)
    {
        ArgumentNullException.ThrowIfNull(session);

        if (_session != null || _inMemoryTransaction)
        {
            throw new InvalidOperationException("A session is already active for this DbContext session.");
        }

        _session = session;
        _ownsSession = false;
    }

    public async Task CommitTransactionAsync()
    {
        if (_inMemoryTransaction)
        {
            _inMemoryTransaction = false;
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

    public void Dispose()
    {
        DisposeAsync().AsTask().GetAwaiter().GetResult();
        GC.SuppressFinalize(this);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (Transaction.IsActive)
        {
            await AbortTransactionAsync();
        }

        GC.SuppressFinalize(this);
    }

    protected void EnsureTransactionActive()
    {
        if (!Transaction.IsActive)
        {
            throw new InvalidOperationException("A transaction is required to save changes. Start a session with StartSession() first.");
        }

        if (_session != null && !_session.IsInTransaction)
        {
            if (_dbContext.Options.Conventions.TransactionSupportBehavior == TransactionSupportBehavior.Simulate)
            {
                _inMemoryTransaction = true;
                return;
            }

            throw new InvalidOperationException("MongoDB commits require an active transaction.");
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
        if (_transactionsSupported.HasValue)
        {
            return _transactionsSupported.Value;
        }

        var database = _dbContext.Options.Mongo ?? throw new InvalidOperationException("Mongo database not configured. This is not supposed to happen and is likely a bug.");

        if (database.Client is MongoClient mongoClient)
        {
            var clusterType = mongoClient.Cluster.Description.Type;
            if (clusterType == ClusterType.ReplicaSet || clusterType == ClusterType.Sharded)
            {
                _transactionsSupported = true;
                return true;
            }
        }

        var hello = database.RunCommand<BsonDocument>(new BsonDocument("hello", 1));
        var isSharded = hello.TryGetValue("msg", out var msg) && msg == "isdbgrid";
        var isReplicaSet = hello.TryGetValue("setName", out _);
        _transactionsSupported = isReplicaSet || isSharded;
        return _transactionsSupported.Value;
    }

    private void HandleUnsupportedTransactions()
    {
        if (_dbContext.Options.Conventions.TransactionSupportBehavior == TransactionSupportBehavior.Simulate)
        {
            _inMemoryTransaction = true;
            return;
        }

        throw new InvalidOperationException(
            "MongoDB transactions require a replica set or sharded cluster. Configure Conventions.TransactionSupportBehavior to Simulate to fall back to a non-transactional unit of work.");
    }
}
