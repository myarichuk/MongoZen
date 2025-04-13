using System.Linq.Expressions;

namespace Library; 

public abstract class DbContext
{
    protected DbContext()
    {
        OnModelCreating();
    }

    protected virtual void OnModelCreating() { }

    public DbContextSession BeginSession() => new(this);
}