using System.IO;
using System.Text;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoZen;

namespace MongoZen.Tests;

public class GridFSTests : IntegrationTestBase
{
    private class User
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string Name { get; set; } = string.Empty;
    }

    private class TestDbContext(DbContextOptions options) : DbContext(options)
    {
        public IDbSet<User> Users { get; set; } = null!;

        protected override void InitializeDbSets()
        {
            if (Options.UseInMemory)
                Users = new InMemoryDbSet<User>("Users", Options.Conventions);
            else
                Users = new DbSet<User>(Options.Mongo!.GetCollection<User>("Users"), Options.Conventions);
        }

        public override string GetCollectionName(Type entityType)
        {
            if (entityType == typeof(User)) return "Users";
            throw new ArgumentException();
        }
    }

    private sealed class TestDbContextSession(TestDbContext dbContext, bool startTransaction = true)
        : DbContextSession<TestDbContext>(dbContext, startTransaction)
    {
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Can_Store_And_Get_Attachment(bool useInMemory)
    {
        var options = useInMemory ? new DbContextOptions() : new DbContextOptions(Database!);
        var db = new TestDbContext(options);
        await using var session = new TestDbContextSession(db, startTransaction: false);

        var userId = "users/1";
        var fileName = "profile.jpg";
        var content = "fake image content";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(content));

        await session.Attachments.StoreAsync(userId, fileName, stream, "image/jpeg");

        using var downloadStream = await session.Attachments.GetAsync(userId, fileName);
        using var reader = new StreamReader(downloadStream);
        var downloadedContent = await reader.ReadToEndAsync();

        Assert.Equal(content, downloadedContent);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Can_Delete_Attachment(bool useInMemory)
    {
        var options = useInMemory ? new DbContextOptions() : new DbContextOptions(Database!);
        var db = new TestDbContext(options);
        await using var session = new TestDbContextSession(db, startTransaction: false);

        var userId = "users/2";
        var fileName = "doc.pdf";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("pdf content"));

        await session.Attachments.StoreAsync(userId, fileName, stream);
        
        var names = await session.Attachments.GetNamesAsync(userId);
        Assert.Single(names);

        await session.Attachments.DeleteAsync(userId, fileName);

        names = await session.Attachments.GetNamesAsync(userId);
        Assert.Empty(names);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Can_List_Attachments(bool useInMemory)
    {
        var options = useInMemory ? new DbContextOptions() : new DbContextOptions(Database!);
        var db = new TestDbContext(options);
        await using var session = new TestDbContextSession(db, startTransaction: false);

        var userId = "users/3";
        
        await session.Attachments.StoreAsync(userId, "file1.txt", new MemoryStream(Encoding.UTF8.GetBytes("c1")));
        await session.Attachments.StoreAsync(userId, "file2.txt", new MemoryStream(Encoding.UTF8.GetBytes("c2")));

        var names = (await session.Attachments.GetNamesAsync(userId)).ToList();

        Assert.Equal(2, names.Count);
        Assert.Contains(names, x => x.Name == "file1.txt");
        Assert.Contains(names, x => x.Name == "file2.txt");
    }

    [Theory]
    [InlineData(255 * 1024)] // Exactly one chunk
    [InlineData(255 * 1024 + 1)] // One chunk and one byte
    [InlineData(255 * 1024 * 2)] // Exactly two chunks
    public async Task Chunk_Boundary_Tests(int size)
    {
        var options = new DbContextOptions(Database!);
        var db = new TestDbContext(options);
        await using var session = new TestDbContextSession(db, startTransaction: false);

        var userId = "users/boundary";
        var fileName = $"file_{size}.bin";
        var data = new byte[size];
        new Random().NextBytes(data);

        await session.Attachments.StoreAsync(userId, fileName, new MemoryStream(data));

        using var downloadStream = await session.Attachments.GetAsync(userId, fileName);
        var downloadedData = new byte[size];
        int read = 0;
        while (read < size)
        {
            read += await downloadStream.ReadAsync(downloadedData, read, size - read);
        }

        Assert.Equal(data, downloadedData);
    }

    [Fact]
    public async Task Large_File_Test()
    {
        var options = new DbContextOptions(Database!);
        var db = new TestDbContext(options);
        await using var session = new TestDbContextSession(db, startTransaction: false);

        var userId = "users/large";
        var fileName = "big.zip";
        int size = 1024 * 1024 * 5; // 5MB
        var data = new byte[size];
        new Random().NextBytes(data);

        await session.Attachments.StoreAsync(userId, fileName, new MemoryStream(data));

        using var downloadStream = await session.Attachments.GetAsync(userId, fileName);
        var downloadedData = new byte[size];
        int read = 0;
        while (read < size)
        {
            var r = await downloadStream.ReadAsync(downloadedData, read, size - read);
            if (r == 0) break;
            read += r;
        }

        Assert.Equal(size, read);
        Assert.Equal(data, downloadedData);
    }

    [Fact]
    public async Task Attachments_Participate_In_Transaction_Rollback()
    {
        // GridFS transactions require a replica set. IntegrationTestBase provides one.
        var options = new DbContextOptions(Database!);
        var db = new TestDbContext(options);
        
        // 1. Start session with transaction
        await using var session = new TestDbContextSession(db, startTransaction: true);
        await session.Advanced.InitializeAsync();

        var userId = "users/4";
        var fileName = "secret.txt";
        
        // 2. Store attachment within transaction
        await session.Attachments.StoreAsync(userId, fileName, new MemoryStream(Encoding.UTF8.GetBytes("secret")));

        // 3. Abort transaction
        await session.AbortTransactionAsync();

        // 4. Verify attachment is GONE
        // We need a new session or check without transaction to be sure
        await using var session2 = new TestDbContextSession(db, startTransaction: false);
        var names = await session2.Attachments.GetNamesAsync(userId);
        Assert.Empty(names);
    }

    [Fact]
    public async Task Overwriting_Attachment_Works()
    {
        var options = new DbContextOptions(Database!);
        var db = new TestDbContext(options);
        await using var session = new TestDbContextSession(db, startTransaction: false);

        var userId = "users/5";
        var fileName = "config.xml";

        await session.Attachments.StoreAsync(userId, fileName, new MemoryStream(Encoding.UTF8.GetBytes("v1")));
        await session.Attachments.StoreAsync(userId, fileName, new MemoryStream(Encoding.UTF8.GetBytes("v2")));

        using var downloadStream = await session.Attachments.GetAsync(userId, fileName);
        using var reader = new StreamReader(downloadStream);
        var content = await reader.ReadToEndAsync();

        Assert.Equal("v2", content);
        
        var names = await session.Attachments.GetNamesAsync(userId);
        Assert.Single(names); // Should not have duplicates in GridFS
    }
}
