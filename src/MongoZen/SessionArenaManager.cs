using SharpArena.Allocators;

namespace MongoZen;

internal class SessionArenaManager : IDisposable
{
    public ArenaAllocator Current { get; private set; }
    public ArenaAllocator Next { get; private set; }
    public int Generation { get; private set; }

    public SessionArenaManager()
    {
        Current = new ArenaAllocator();
        Next = new ArenaAllocator();
    }

    public void IncrementGeneration() => Generation++;

    public void SwapAndResetNext()
    {
        var temp = Current;
        Current = Next;
        Next = temp;
        Next.Reset();
    }

    public void ResetAll()
    {
        Current.Reset();
        Next.Reset();
        Generation++;
    }

    public void Dispose()
    {
        Current.Dispose();
        Next.Dispose();
    }
}
