using MongoZen;

namespace Playground;

public class MyDbContext : DbContext
{
    public IDbSet<Person> People { get; set; } = null!;

    public MyDbContext(DbContextOptions options)
        : base(options)
    {
    }
}
