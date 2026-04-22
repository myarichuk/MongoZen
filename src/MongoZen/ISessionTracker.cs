namespace MongoZen;

public interface ISessionTracker
{
    TEntity Track<TEntity>(TEntity entity, object id) where TEntity : class;
    IEnumerable<TEntity> GetDirtyEntities<TEntity>() where TEntity : class;
    void Untrack<TEntity>(object id);
    void ClearTracking();
}
