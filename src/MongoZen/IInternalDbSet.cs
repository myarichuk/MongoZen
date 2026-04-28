using System.Threading;
using System.Threading.Tasks;

namespace MongoZen;

internal interface IInternalDbSet<T> where T : class
{
    ValueTask CommitAsync(CommitContext<T> context, CancellationToken cancellationToken = default);
}
