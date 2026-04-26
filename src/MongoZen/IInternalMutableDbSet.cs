using System.ComponentModel;

namespace MongoZen;

[EditorBrowsable(EditorBrowsableState.Never)]
public interface IInternalMutableDbSet : IDisposable
{
    ValueTask CommitAsync(TransactionContext transaction, CancellationToken cancellationToken = default);
    void ClearTracking();
    void RefreshShadows(ISessionTracker tracker);
}
