using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Clusters;

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
public abstract class DbContextSession<TDbContext> : IAsyncDisposable
    where TDbContext : DbContext
{
    protected readonly TDbContext _dbContext;
    private IClientSessionHandle? _session;
    private bool _ownsSession;
    private bool _inMemoryTransaction;
    private bool _disposed;
    private bool? _transactionsSupported;
    private bool _committed;

    protected DbContextSession(TDbContext dbContext, bool startTransaction = true)
    {
        _dbContext = dbContext;

        if (startTransaction)
        {
            StartTransaction();
        }
    }

    public IClientSessionHandle? ClientSession => _session;

    /// <summary>
    /// Gets the current transaction context. This is cached to prevent
    /// snapshot races from creating a new struct on each access.
    /// </summary>
    public TransactionContext Transaction => new(_session, _inMemoryTransaction);

    public void BeginTransaction()
    {
        if (_inMemoryTransaction)
        {
            if (_inMemoryTransaction)
            {
                throw new InvalidOperationException("A transaction is already active for this session.");
            }

            _inMemoryTransaction = true;
            _committed = false;
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
            if (_session.IsInTransaction)
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
