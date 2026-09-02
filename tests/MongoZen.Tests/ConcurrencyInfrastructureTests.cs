using Xunit;

namespace MongoZen.Tests;

public class ConcurrencyInfrastructureTests
{
    [Fact]
    public void ConcurrencyException_CapturesEntity()
    {
        var entity = new { Id = "test" };
        var message = "Concurrency failure";
        
        var ex = new ConcurrencyException(message, entity);
        
        Assert.Equal(message, ex.Message);
        Assert.Same(entity, ex.Entity);
    }

    [Fact]
    public void ConcurrencyCheckAttribute_CanBeApplied()
    {
        // This is mostly a compile-time check, but we can verify it exists
        var attr = new ConcurrencyCheckAttribute();
        Assert.NotNull(attr);
    }
}
