namespace MongoZen;

/// <summary>
/// Base class for generated DbContext session wrappers that coordinate mutable DbSets.
/// </summary>
/// <typeparam name="TDbContext">The DbContext type wrapped by the session.</typeparam>
public abstract class DbContextSession<TDbContext>
    where TDbContext : DbContext
{
    protected readonly TDbContext _dbContext;

    /// <summary>
    /// Initializes a new instance of the <see cref="DbContextSession{TDbContext}"/> class.
    /// </summary>
    /// <param name="dbContext">The DbContext instance to wrap.</param>
    protected DbContextSession(TDbContext dbContext) => _dbContext = dbContext;
}
