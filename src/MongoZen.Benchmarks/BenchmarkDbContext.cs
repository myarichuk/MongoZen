using MongoZen;

namespace MongoZen.Benchmarks;

public partial class BenchmarkDbContext(DbContextOptions options) : DbContext(options)
{
    public IDbSet<BenchmarkEntity> Entities { get; set; } = null!;
}
