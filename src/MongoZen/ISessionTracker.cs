using System;
using System.Collections.Generic;

namespace MongoZen;

public interface ISessionTracker
{
    SharpArena.Allocators.ArenaAllocator Arena { get; }

    TEntity Track<TEntity>(
        TEntity entity, 
        object id, 
        Func<TEntity, SharpArena.Allocators.ArenaAllocator, IntPtr> materializer, 
        Func<TEntity, IntPtr, bool> differ) where TEntity : class;

    IEnumerable<TEntity> GetDirtyEntities<TEntity>() where TEntity : class;
    
    bool TryGetEntity<TEntity>(object id, out TEntity? entity) where TEntity : class;

    void Untrack<TEntity>(object id) where TEntity : class;
    
    void ClearTracking();

    void TrackDynamic(object entity, Type entityType, object id);

    void RefreshShadows<TEntity>(
        Func<TEntity, SharpArena.Allocators.ArenaAllocator, IntPtr> materializer,
        Action<TEntity>? versionIncrementer = null) where TEntity : class;
}
