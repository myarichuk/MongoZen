using System.Runtime.CompilerServices;
using MongoDB.Driver;
using SharpArena.Allocators;
using MongoZen.Collections;

namespace MongoZen;

internal interface IEntityTracker : IDisposable
{
    void RefreshShadows(ArenaAllocator arena, int generation);
    void TrackDynamic(object entity, in DocId id);
    bool TryGetEntity(in DocId id, out object? entity);
    bool TryGetShadowPtr(in DocId id, out ShadowPtr shadowPtr);
    void Untrack(in DocId id);
    void CollectDirtyEntities<T>(PooledList<T> buffer, int currentGeneration) where T : class;
    IEnumerable<TEntity> GetDirtyEntities<TEntity>(int currentGeneration) where TEntity : class;
    void Reset();
}

internal class EntityTracker<TEntity> : IEntityTracker where TEntity : class
{
    private readonly Func<TEntity, IntPtr, bool>? _differ;
    private readonly Func<TEntity, ArenaAllocator, IntPtr>? _materializer;
    public PooledDictionary<DocId, (TEntity Entity, ShadowPtr ShadowPtr)> Map;

    public EntityTracker()
    {
        Map = new PooledDictionary<DocId, (TEntity Entity, ShadowPtr ShadowPtr)>();
    }

    public EntityTracker(Func<TEntity, IntPtr, bool>? differ, Func<TEntity, ArenaAllocator, IntPtr>? materializer)
    {
        _differ = differ;
        _materializer = materializer;
        Map = new PooledDictionary<DocId, (TEntity Entity, ShadowPtr ShadowPtr)>();
    }

    public void RefreshShadows(ArenaAllocator arena, int generation)
    {
        if (_materializer == null || Map.Count == 0) return;

        Map.UpdateAllValues((_, entry) =>
        {
            var newShadowPtr = new ShadowPtr(_materializer(entry.Entity, arena), generation);
            return (entry.Entity, newShadowPtr);
        });
    }

    public void TrackDynamic(object entity, in DocId id)
    {
        if (!Map.TryGetValue(id, out _))
        {
            Map[id] = ((TEntity)entity, ShadowPtr.Zero);
        }
    }

    public bool TryGetEntity(in DocId id, out object? entity)
    {
        if (Map.TryGetValue(id, out var entry))
        {
            entity = entry.Entity;
            return true;
        }
        entity = null;
        return false;
    }

    public bool TryGetShadowPtr(in DocId id, out ShadowPtr shadowPtr)
    {
        if (Map.TryGetValue(id, out var entry))
        {
            shadowPtr = entry.ShadowPtr;
            return true;
        }
        shadowPtr = ShadowPtr.Zero;
        return false;
    }

    public void Untrack(in DocId id) => Map.Remove(id);

    public void CollectDirtyEntities<T>(PooledList<T> buffer, int currentGeneration) where T : class
    {
        if (_differ == null) return;

        foreach (var kvp in Map)
        {
            var entry = kvp.Value;
            if (entry.ShadowPtr.IsZero) continue;
#if DEBUG
            if (entry.ShadowPtr.Generation != currentGeneration)
            {
                throw new InvalidOperationException("Attempted to access a shadow pointer from a previous arena generation. This pointer is stale and unsafe to use.");
            }
#endif
            if (_differ(entry.Entity, entry.ShadowPtr))
            {
                buffer.Add((T)(object)entry.Entity);
            }
        }
    }

    public IEnumerable<TActualEntity> GetDirtyEntities<TActualEntity>(int currentGeneration) where TActualEntity : class
    {
        if (_differ == null) yield break;

        foreach (var kvp in Map)
        {
            var entry = kvp.Value;
            if (entry.ShadowPtr.IsZero) continue;
#if DEBUG
            if (entry.ShadowPtr.Generation != currentGeneration)
            {
                throw new InvalidOperationException("Attempted to access a shadow pointer from a previous arena generation. This pointer is stale and unsafe to use.");
            }
#endif
            if (_differ(entry.Entity, entry.ShadowPtr))
            {
                yield return (TActualEntity)(object)entry.Entity;
            }
        }
    }

    public TEntity Track(TEntity entity, in DocId id, bool forceShadow, ArenaAllocator arena, int generation)
    {
        if (Map.TryGetValue(id, out var existing))
        {
            return existing.Entity;
        }

        ShadowPtr shadowPtr = ShadowPtr.Zero;
        if (forceShadow && _materializer != null)
        {
            shadowPtr = new ShadowPtr(_materializer(entity, arena), generation);
        }

        Map[id] = (entity, shadowPtr);
        return entity;
    }

    public void Reset()
    {
        Map.Dispose();
        // Map will be lazily re-initialized on next use.
    }

    public void Dispose() => Map.Dispose();
}
