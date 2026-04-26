using System.ComponentModel;

namespace MongoZen;

[EditorBrowsable(EditorBrowsableState.Never)]
public interface IInternalMutableDbSet
{
    Task CommitAsync(TransactionContext transaction, CancellationToken cancellationToken = default);
}
