namespace MongoZen;

public abstract class DbContextSession<TDbContext>
    where TDbContext : DbContext
{
    protected readonly TDbContext _dbContext;

    protected DbContextSession(TDbContext dbContext) => _dbContext = dbContext;
}