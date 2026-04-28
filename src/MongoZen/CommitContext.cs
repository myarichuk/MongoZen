using System;
using System.Collections.Generic;
using MongoDB.Driver;
using MongoZen.Collections;
using SharpArena.Allocators;

namespace MongoZen;

internal readonly struct CommitWork<T> where T : class
{
    public readonly IEnumerable<T> Added;
    public readonly IEnumerable<T> Removed;
    public readonly IEnumerable<object> RemovedIds;
    public readonly IEnumerable<T> Updated;
    public readonly IEnumerable<T> Dirty;

    public CommitWork(
        IEnumerable<T> added,
        IEnumerable<T> removed,
        IEnumerable<object> removedIds,
        IEnumerable<T> updated,
        IEnumerable<T> dirty)
    {
        Added = added;
        Removed = removed;
        RemovedIds = removedIds;
        Updated = updated;
        Dirty = dirty;
    }
}

internal class CommitBuffers<T> where T : class
{
    public PooledDictionary<DocId, (T Entity, bool IsDirty)> UpsertBuffer;
    public PooledHashSet<object> RawIdBuffer;
    public PooledList<WriteModel<T>> ModelBuffer;

    public CommitBuffers(
        PooledDictionary<DocId, (T Entity, bool IsDirty)> upsertBuffer,
        PooledHashSet<object> rawIdBuffer,
        PooledList<WriteModel<T>> modelBuffer)
    {
        UpsertBuffer = upsertBuffer;
        RawIdBuffer = rawIdBuffer;
        ModelBuffer = modelBuffer;
    }
}

internal readonly struct SessionState
{
    public readonly ISessionTracker Tracker;
    public readonly TransactionContext Transaction;
    public readonly ArenaAllocator Arena;

    public SessionState(ISessionTracker tracker, TransactionContext transaction, ArenaAllocator arena)
    {
        Tracker = tracker;
        Transaction = transaction;
        Arena = arena;
    }
}

internal class CommitContext<T> where T : class
{
    public CommitWork<T> Work;
    public CommitBuffers<T> Buffers;
    public SessionState Session;
    public Func<T, IntPtr, UpdateDefinition<T>?>? Extractor;

    public CommitContext(
        in CommitWork<T> work,
        CommitBuffers<T> buffers,
        in SessionState session,
        Func<T, IntPtr, UpdateDefinition<T>?>? extractor)
    {
        Work = work;
        Buffers = buffers;
        Session = session;
        Extractor = extractor;
    }
}
