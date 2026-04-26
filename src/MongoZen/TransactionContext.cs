using MongoDB.Driver;

namespace MongoZen;

public readonly struct TransactionContext(IClientSessionHandle? session, bool isInMemoryTransaction)
{
    public IClientSessionHandle? Session { get; } = session;

    public bool IsInMemoryTransaction { get; } = isInMemoryTransaction;

    public bool IsActive => IsInMemoryTransaction || (Session != null && Session.IsInTransaction);

    public static TransactionContext FromSession(IClientSessionHandle session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return new TransactionContext(session, isInMemoryTransaction: false);
    }

    public static TransactionContext InMemory()
        => new TransactionContext(session: null, isInMemoryTransaction: true);
}
