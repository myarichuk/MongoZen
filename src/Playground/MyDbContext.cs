using MongoZen;

namespace Playground;

public partial class MyDbContext(DbContextOptions options) : DbContext(options)
{
    public IDbSet<Person> People { get; set; } = null!;
}
