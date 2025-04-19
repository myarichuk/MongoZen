namespace Library;

public abstract class DbContextSession<TDbContext>
    where TDbContext : DbContext
{
    private readonly TDbContext _dbContext;

    public DbContextSession(TDbContext dbContext) => _dbContext = dbContext;
}