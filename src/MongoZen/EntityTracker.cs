using System.Runtime.CompilerServices;
using MongoDB.Driver;
using SharpArena.Allocators;
using MongoZen.Collections;

namespace MongoZen;

internal interface IEntityTracker : IDisposable
{
    void RefreshShadows(ArenaAllocator arena, int generation);
    void TrackDynamic(object entity, object id);
    bool TryGetEntity(object id, out object? entity);
    bool TryGetShadowPtr(object id, out ShadowPtr shadowPtr);
    void Untrack(object id);
}

internal class EntityTracker<TEntity> : IEntityTracker where TEntity : class
{
    private readonly Func<TEntity, IntPtr, bool>? _differ;
    private readonly Func<TEntity, ArenaAllocator, IntPtr>? _materializer;
    public PooledDictionary<object, (TEntity Entity, ShadowPtr ShadowPtr)> Map;

    public EntityTracker()
    {
        Map = new PooledDictionary<object, (TEntity Entity, ShadowPtr ShadowPtr)>(16);
    }

    public EntityTracker(Func<TEntity, IntPtr, bool>? differ, Func<TEntity, ArenaAllocator, IntPtr>? materializer)
        : this()
    {
        _differ = differ;
        _materializer = materializer;
    }

    public void RefreshShadows(ArenaAllocator arena, int generation)
    {
        if (_materializer == null) return;

        foreach (var kvp in Map)
        {
            var entry = kvp.Value;
            var newShadowPtr = new ShadowPtr(_materializer(entry.Entity, arena), generation);
            Map[kvp.Key] = (entry.Entity, newShadowPtr);
        }
    }

    public void TrackDynamic(object entity, object id)
    {
        if (!Map.ContainsKey(id))
        {
            Map[id] = ((TEntity)entity, ShadowPtr.Zero);
        }
    }

    public bool TryGetEntity(object id, out object? entity)
    {
        if (Map.TryGetValue(id, out var entry))
        {
            entity = entry.Entity;
            return true;
        }
        entity = null;
        return false;
    }

    public bool TryGetShadowPtr(object id, out ShadowPtr shadowPtr)
    {
        if (Map.TryGetValue(id, out var entry))
        {
            shadowPtr = entry.ShadowPtr;
            return true;
        }
        shadowPtr = ShadowPtr.Zero;
        return false;
    }

    public void Untrack(object id) => Map.Remove(id);

    public IEnumerable<TEntity> GetDirtyEntities(int currentGeneration)
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
                yield return entry.Entity;
            }
        }
    }

    public TEntity Track(TEntity entity, object id, bool forceShadow, ArenaAllocator arena, int generation)
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

    public void Dispose() => Map.Dispose();
}
