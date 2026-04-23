using System;
using System.Collections.Generic;

namespace MongoZen;

public interface ISessionTracker
{
    TEntity Track<TEntity>(
        TEntity entity, 
        object id, 
        Func<TEntity, SharpArena.Allocators.ArenaAllocator, IntPtr> materializer, 
        Func<TEntity, IntPtr, bool> differ) where TEntity : class;

    IEnumerable<TEntity> GetDirtyEntities<TEntity>() where TEntity : class;
    
    void Untrack<TEntity>(object id);
    
    void ClearTracking();

    SharpArena.Allocators.ArenaAllocator Arena { get; }
}
