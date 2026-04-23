using MongoZen;
using Xunit;
using MongoDB.Driver;

namespace MongoZen.Tests;

public class AttachAndRemoveTests : IntegrationTestBase
{
    public class Product
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string Name { get; set; } = string.Empty;
        public decimal Price { get; set; }
    }

    public partial class ShopContext : DbContext
    {
        public ShopContext(DbContextOptions options) : base(options) { }
        public IDbSet<Product> Products { get; set; } = null!;
    }

    // Hand-written session to simulate what the generator would produce
    // Since we want to test the generator's logic too, but running it in tests is complex, 
    // we already updated the generator. Here we test the MutableDbSet logic directly.
    private sealed class ShopContextSession : DbContextSession<ShopContext>
    {
        public ShopContextSession(ShopContext dbContext) : base(dbContext)
        {
            Products = new MutableDbSet<Product>(
                _dbContext.Products, 
                () => Transaction, 
                this, 
                (entity, arena) => { unsafe {
                    var ptr = arena.Alloc((nuint)System.Runtime.CompilerServices.Unsafe.SizeOf<Product_Shadow>()); 
                    ref var s = ref System.Runtime.CompilerServices.Unsafe.AsRef<Product_Shadow>(ptr); 
                    s.From(entity, arena); 
                    return (System.IntPtr)ptr; 
                } },
                (entity, ptr) => { unsafe {
                    ref var s = ref System.Runtime.CompilerServices.Unsafe.AsRef<Product_Shadow>((void*)ptr); 
                    return s.IsDirty(entity); 
                } },
                _dbContext.Options.Conventions);
        }

        public IMutableDbSet<Product> Products { get; }

        public void Add<TEntity>(TEntity entity) where TEntity : class
        {
            if (entity is Product p) Products.Add(p);
        }

        public void Attach<TEntity>(TEntity entity) where TEntity : class
        {
            if (entity is Product p) Products.Attach(p);
        }

        public void Remove<TEntity>(TEntity entity) where TEntity : class
        {
            if (entity is Product p) Products.Remove(p);
        }

        public void Remove<TEntity>(object id) where TEntity : class
        {
            if (typeof(TEntity) == typeof(Product)) Products.Remove(id);
        }

        public async ValueTask<TEntity?> LoadAsync<TEntity>(object id, System.Threading.CancellationToken cancellationToken = default) where TEntity : class
        {
            if (typeof(TEntity) == typeof(Product)) return (TEntity?)(object?)await Products.LoadAsync(id, cancellationToken);
            return null;
        }

        public IMutableDbSet<TEntity> Include<TEntity>(System.Linq.Expressions.Expression<Func<TEntity, object?>> path) where TEntity : class
        {
            if (typeof(TEntity) == typeof(Product)) return (IMutableDbSet<TEntity>)(object)Products.Include((System.Linq.Expressions.Expression<Func<Product, object?>>)(object)path);
            throw new ArgumentException();
        }

        public async ValueTask SaveChangesAsync()
        {
            EnsureTransactionActive();
            await Products.CommitAsync(Transaction);
            await CommitTransactionAsync();
        }
    }

    // Shadow struct for Product (normally generated)
    private struct Product_Shadow
    {
        public bool _hasValue;
        public SharpArena.Collections.ArenaString Name;
        public decimal Price;

        public void From(Product source, SharpArena.Allocators.ArenaAllocator arena)
        {
            _hasValue = true;
            Name = SharpArena.Collections.ArenaString.Clone(source.Name, arena);
            Price = source.Price;
        }

        public bool IsDirty(Product current)
        {
            if (current == null) return _hasValue;
            if (!_hasValue) return true;
            return !Name.Equals(current.Name) || Price != current.Price;
        }
    }

    [Fact]
    public async Task Attach_StartsTrackingExistingEntity()
    {
        var ctx = new ShopContext(new DbContextOptions(Database!));
        var productId = Guid.NewGuid().ToString();
        var product = new Product { Id = productId, Name = "Laptop", Price = 1000m };

        // 1. Seed the database
        await Database!.GetCollection<Product>("Products").InsertOneAsync(product);

        // 2. Attach and modify
        await using (var session = new ShopContextSession(ctx))
        {
            // Create a NEW instance with same ID to simulate loading from elsewhere
            var existing = new Product { Id = productId, Name = "Laptop", Price = 1000m };
            
            session.Attach(existing);
            existing.Price = 1100m; // Change price
            
            await session.SaveChangesAsync();
        }

        // 3. Verify
        var updated = await Database!.GetCollection<Product>("Products").Find(p => p.Id == productId).FirstOrDefaultAsync();
        Assert.Equal(1100m, updated.Price);
    }

    [Fact]
    public async Task Add_AssignsIdAndTracks()
    {
        var ctx = new ShopContext(new DbContextOptions(Database!));
        var product = new Product { Name = "Mouse", Price = 25m };
        product.Id = null!; // Clear ID to force generation

        await using (var session = new ShopContextSession(ctx))
        {
            session.Add(product);
            Assert.NotNull(product.Id); // ID should be generated
            
            product.Name = "Gaming Mouse"; // Modify after Add but before Save
            await session.SaveChangesAsync();
        }

        var saved = await Database!.GetCollection<Product>("Products").Find(p => p.Name == "Gaming Mouse").FirstOrDefaultAsync();
        Assert.NotNull(saved);
        Assert.Equal(product.Id, saved.Id);
    }

    [Fact]
    public async Task Remove_DeletesFromDatabase()
    {
        var ctx = new ShopContext(new DbContextOptions(Database!));
        var p1 = new Product { Name = "K1" };
        var p2 = new Product { Name = "K2" };
        await Database!.GetCollection<Product>("Products").InsertManyAsync(new[] { p1, p2 });

        await using (var session = new ShopContextSession(ctx))
        {
            session.Remove(p1);
            session.Remove(p2);
            await session.SaveChangesAsync();
        }

        var count = await Database!.GetCollection<Product>("Products").CountDocumentsAsync(FilterDefinition<Product>.Empty);
        Assert.Equal(0, count);
    }

    [Fact]
    public async Task LoadAsync_TracksAndReturnsSameInstance()
    {
        var ctx = new ShopContext(new DbContextOptions(Database!));
        var p1 = new Product { Name = "TrackMe" };
        await Database!.GetCollection<Product>("Products").InsertOneAsync(p1);

        await using (var session = new ShopContextSession(ctx))
        {
            var loaded = await session.LoadAsync<Product>(p1.Id);
            Assert.NotNull(loaded);
            Assert.Equal("TrackMe", loaded!.Name);

            var loadedAgain = await session.LoadAsync<Product>(p1.Id);
            Assert.Same(loaded, loadedAgain); // Identity Map check

            loaded.Name = "TrackedChange";
            await session.SaveChangesAsync();
        }

        var updated = await Database!.GetCollection<Product>("Products").Find(p => p.Id == p1.Id).FirstOrDefaultAsync();
        Assert.Equal("TrackedChange", updated.Name);
    }

    [Fact]
    public async Task RemoveById_DeletesFromDatabase()
    {
        var ctx = new ShopContext(new DbContextOptions(Database!));
        var p1 = new Product { Name = "DeleteMe" };
        await Database!.GetCollection<Product>("Products").InsertOneAsync(p1);

        await using (var session = new ShopContextSession(ctx))
        {
            session.Remove<Product>(p1.Id);
            await session.SaveChangesAsync();
        }

        var count = await Database!.GetCollection<Product>("Products").CountDocumentsAsync(FilterDefinition<Product>.Empty);
        Assert.Equal(0, count);
    }
}
