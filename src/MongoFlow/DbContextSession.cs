namespace MongoFlow;

public abstract class DbContextSession<TDbContext>
    where TDbContext : DbContext
{
    protected readonly TDbContext _dbContext;

    public DbContextSession(TDbContext dbContext) => _dbContext = dbContext;
}