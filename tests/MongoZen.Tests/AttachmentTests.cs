using System.Text;
using MongoDB.Driver;
using Xunit;

namespace MongoZen.Tests;

public class AttachmentTests : IntegrationTestBase
{
    [Fact]
    public async Task Session_Should_Store_And_Get_Attachment()
    {
        var db = Database;
        var store = new DocumentStore(db.Client, db.DatabaseNamespace.DatabaseName);
        using var session = store.OpenSession();

        var documentId = "doc/1";
        var attachmentName = "hello.txt";
        var content = "Hello MongoZen!";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(content));

        // Store
        await session.Attachments.StoreAsync(documentId, attachmentName, stream, "text/plain");

        // Get Names
        var names = await session.Attachments.GetNamesAsync(documentId);
        Assert.Contains(attachmentName, names);

        // Get Content
        using var result = await session.Attachments.GetAsync(documentId, attachmentName);
        Assert.Equal(attachmentName, result.Name);
        Assert.Equal("text/plain", result.ContentType);
        
        using var reader = new StreamReader(result.Stream);
        var actualContent = await reader.ReadToEndAsync();
        Assert.Equal(content, actualContent);
    }

    [Fact]
    public async Task Session_Should_Delete_Attachment()
    {
        var db = Database;
        var store = new DocumentStore(db.Client, db.DatabaseNamespace.DatabaseName);
        using var session = store.OpenSession();

        var documentId = "doc/2";
        var attachmentName = "delete-me.bin";
        using var stream = new MemoryStream([1, 2, 3]);

        await session.Attachments.StoreAsync(documentId, attachmentName, stream);
        
        var names = await session.Attachments.GetNamesAsync(documentId);
        Assert.Contains(attachmentName, names);

        // Delete
        await session.Attachments.DeleteAsync(documentId, attachmentName);

        names = await session.Attachments.GetNamesAsync(documentId);
        Assert.DoesNotContain(attachmentName, names);
    }

    [Fact]
    public async Task Session_Should_Cascade_Delete_Attachments()
    {
        var db = Database;
        var store = new DocumentStore(db.Client, db.DatabaseNamespace.DatabaseName);
        
        var entity = new SimpleEntity { Id = 100, Name = "Parent" };
        var collectionName = DocumentTypeTracker.GetDefaultCollectionName(typeof(SimpleEntity));
        await db.GetCollection<SimpleEntity>(collectionName).InsertOneAsync(entity);

        using var session = store.OpenSession();
        var loaded = await session.LoadAsync<SimpleEntity>(100);
        Assert.NotNull(loaded);

        // Add attachment
        using var stream = new MemoryStream([4, 5, 6]);
        await session.Attachments.StoreAsync(100, "orphan.bin", stream);

        // Delete parent
        session.Delete(loaded);
        await session.SaveChangesAsync();

        // Verify attachment is gone
        var names = await session.Attachments.GetNamesAsync(100);
        Assert.Empty(names);
    }

    [Fact]
    public async Task Session_Should_Rollback_Attachments()
    {
        var db = Database;
        var store = new DocumentStore(db.Client, db.DatabaseNamespace.DatabaseName);
        
        var documentId = "tx/1";
        var attachmentName = "secret.txt";
        
        try
        {
            using var session = store.OpenSession();
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes("shhh"));

            await session.Attachments.StoreAsync(documentId, attachmentName, stream);
            
            // Abort the transaction explicitly or throw
            throw new Exception("Simulated failure");
        }
        catch
        {
            // Expected
        }

        // Verify attachment was not saved
        using var newSession = store.OpenSession();
        var names = await newSession.Attachments.GetNamesAsync(documentId);
        Assert.Empty(names);
    }
}
