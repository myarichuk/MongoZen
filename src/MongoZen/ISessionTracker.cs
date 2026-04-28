using System;
using System.Collections.Generic;

namespace MongoZen;

public interface ISessionTracker
{
    SharpArena.Allocators.ArenaAllocator Arena { get; }
    int Generation { get; }

    TEntity Track<TEntity>(
        TEntity entity, 
        in DocId id, 
        Func<TEntity, SharpArena.Allocators.ArenaAllocator, IntPtr> materializer, 
        Func<TEntity, IntPtr, bool> differ,
        bool forceShadow = true) where TEntity : class;

    IEnumerable<TEntity> GetDirtyEntities<TEntity>() where TEntity : class;
    void CollectDirtyEntities<TEntity>(Collections.PooledList<TEntity> buffer) where TEntity : class;
    
    bool TryGetEntity<TEntity>(in DocId id, out TEntity? entity) where TEntity : class;

    void Untrack<TEntity>(in DocId id) where TEntity : class;
    
    void ClearTracking();

    void TrackDynamic(object entity, Type entityType, in DocId id);

    bool TryGetShadowPtr<TEntity>(in DocId id, out ShadowPtr shadowPtr) where TEntity : class;

    void RefreshShadows<TEntity>(
        Func<TEntity, SharpArena.Allocators.ArenaAllocator, IntPtr> materializer,
        Action<TEntity>? versionIncrementer = null) where TEntity : class;
}
