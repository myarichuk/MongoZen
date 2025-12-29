using MongoDB.Driver;

namespace MongoZen;

public readonly struct TransactionContext
{
    public TransactionContext(IClientSessionHandle? session, bool isInMemoryTransaction)
    {
        Session = session;
        IsInMemoryTransaction = isInMemoryTransaction;
    }

    public IClientSessionHandle? Session { get; }

    public bool IsInMemoryTransaction { get; }

    public bool IsActive => Session != null || IsInMemoryTransaction;

    public static TransactionContext FromSession(IClientSessionHandle session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return new TransactionContext(session, isInMemoryTransaction: false);
    }

    public static TransactionContext InMemory()
        => new TransactionContext(session: null, isInMemoryTransaction: true);
}
