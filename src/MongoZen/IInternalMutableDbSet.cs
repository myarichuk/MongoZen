using System.ComponentModel;

namespace MongoZen;

[EditorBrowsable(EditorBrowsableState.Never)]
public interface IInternalMutableDbSet
{
    ValueTask CommitAsync(SharpArena.Allocators.ArenaAllocator arena, MongoDB.Driver.IClientSessionHandle? session, CancellationToken cancellationToken = default);
    void ClearTracking();
}
