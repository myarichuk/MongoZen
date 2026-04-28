using SharpArena.Allocators;
using System.Collections.Concurrent;

namespace MongoZen;

internal class SessionArenaManager : IDisposable
{
    private static readonly ConcurrentStack<ArenaAllocator> Pool = new();

    public ArenaAllocator Current { get; private set; }
    public int Generation { get; private set; }

    public SessionArenaManager()
    {
        if (!Pool.TryPop(out var arena))
        {
            arena = new ArenaAllocator();
        }
        Current = arena;
    }

    public void IncrementGeneration() => Generation++;

    public void ResetAll()
    {
        Current.Reset();
        Generation++;
    }

    public void Dispose()
    {
        if (Current != null)
        {
            Current.Reset();
            Pool.Push(Current);
            Current = null!;
        }
    }
}
