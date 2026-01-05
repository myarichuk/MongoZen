using MongoDB.Driver;

namespace MongoZen;

public abstract class DbContextSession<TDbContext> : IDisposable, IAsyncDisposable
    where TDbContext : DbContext
{
    protected readonly TDbContext _dbContext;
    private IClientSessionHandle? _session;
    private bool _ownsSession;
    private bool _inMemoryTransaction;
    private bool _disposed;

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
}
