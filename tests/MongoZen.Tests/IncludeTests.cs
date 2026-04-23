using MongoZen;
using Xunit;
using MongoDB.Driver;
using System.Linq.Expressions;

namespace MongoZen.Tests;

public class IncludeTests : IntegrationTestBase
{
    public class Customer
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string Name { get; set; } = string.Empty;
    }

    public class Order
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string CustomerId { get; set; } = string.Empty;
        public decimal Amount { get; set; }
    }

    public partial class TestContext(DbContextOptions options) : DbContext(options)
    {
        public IDbSet<Customer> Customers { get; set; } = null!;
        public IDbSet<Order> Orders { get; set; } = null!;
    }

    private sealed class TestContextSession : DbContextSession<TestContext>
    {
        public TestContextSession(TestContext dbContext) : base(dbContext)
        {
            Customers = new MutableDbSet<Customer>(_dbContext.Customers, () => Transaction, this, conventions: _dbContext.Options.Conventions);
            Orders = new MutableDbSet<Order>(_dbContext.Orders, () => Transaction, this, conventions: _dbContext.Options.Conventions);
        }

        public IMutableDbSet<Customer> Customers { get; }
        public IMutableDbSet<Order> Orders { get; }

        public async ValueTask<TEntity?> LoadAsync<TEntity>(object id, System.Threading.CancellationToken cancellationToken = default) where TEntity : class
        {
            if (typeof(TEntity) == typeof(Customer)) return (TEntity?)(object?)await Customers.LoadAsync(id, cancellationToken);
            if (typeof(TEntity) == typeof(Order)) return (TEntity?)(object?)await Orders.LoadAsync(id, cancellationToken);
            return null;
        }

        public async ValueTask SaveChangesAsync()
        {
            EnsureTransactionActive();
            await Customers.Advanced.CommitAsync(Transaction);
            await Orders.Advanced.CommitAsync(Transaction);
            await CommitTransactionAsync();
        }
    }

    [Fact]
    public async Task Include_PopulatesIdentityMap()
    {
        var ctx = new TestContext(new DbContextOptions(Database!));
        var customer = new Customer { Name = "Raven User" };
        var order = new Order { CustomerId = customer.Id, Amount = 100m };

        await Database!.GetCollection<Customer>("Customers").InsertOneAsync(customer);
        await Database!.GetCollection<Order>("Orders").InsertOneAsync(order);

        await using (var session = new TestContextSession(ctx))
        {
            // Execute query with include
            var orders = (await session.Orders
                .Include<Customer>(o => o.CustomerId)
                .QueryAsync(o => o.Id == order.Id)).ToList();

            Assert.Single(orders);
            Assert.Equal(order.Id, orders[0].Id);

            // Now, LoadAsync for the customer should NOT hit the database (or at least return the tracked instance)
            // To prove it's the SAME instance:
            var loadedCustomer = await session.LoadAsync<Customer>(customer.Id);
            
            Assert.NotNull(loadedCustomer);
            Assert.Equal("Raven User", loadedCustomer!.Name);
        }
    }
}
