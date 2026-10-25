using System.Text;
using MongoDB.Driver;
using Xunit;
using Xunit.Sdk;

namespace MongoZen.Tests;

public class AttachmentTests : TestcontainersIntegrationTestBase
{
    [SkippableFact]
    public async Task Session_Should_Store_And_Get_Attachment()
    {
        Skip.IfNot(SkipReason is null, SkipReason);
        var db = Database;
        var store = new DocumentStore(db.Client, db.DatabaseNamespace.DatabaseName);
        using var session = store.OpenSession();

        var documentId = "doc/1";
        var attachmentName = "hello.txt";
        var content = "Hello MongoZen!";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(content));

        // Store, then commit via SaveChangesAsync — an attachment write is part of the unit of work
        // and is not durable until the session saves.
        await session.Attachments.StoreAsync(documentId, attachmentName, stream, "text/plain");
        await session.SaveChangesAsync();

        // Read back from a *fresh* session so the assertion proves the write was committed, not
        // merely visible inside the writing session's own transaction.
        using var readSession = store.OpenSession();
        var names = await readSession.Attachments.GetNamesAsync(documentId);
        Assert.Contains(attachmentName, names);

        // Get Content
        using var result = await readSession.Attachments.GetAsync(documentId, attachmentName);
        Assert.Equal(attachmentName, result.Name);
        Assert.Equal("text/plain", result.ContentType);

        using var reader = new StreamReader(result.Stream);
        var actualContent = await reader.ReadToEndAsync();
        Assert.Equal(content, actualContent);
    }

    [SkippableFact]
    public async Task Session_Should_Delete_Attachment()
    {
        Skip.IfNot(SkipReason is null, SkipReason);
        var db = Database;
        var store = new DocumentStore(db.Client, db.DatabaseNamespace.DatabaseName);
        using var session = store.OpenSession();

        var documentId = "doc/2";
        var attachmentName = "delete-me.bin";
        using var stream = new MemoryStream([1, 2, 3]);

        await session.Attachments.StoreAsync(documentId, attachmentName, stream);
        await session.SaveChangesAsync();

        var names = await session.Attachments.GetNamesAsync(documentId);
        Assert.Contains(attachmentName, names);

        // Delete, commit, and verify from a fresh session.
        await session.Attachments.DeleteAsync(documentId, attachmentName);
        await session.SaveChangesAsync();

        using var verifySession = store.OpenSession();
        names = await verifySession.Attachments.GetNamesAsync(documentId);
        Assert.DoesNotContain(attachmentName, names);
    }

    [SkippableFact]
    public async Task Session_Should_Cascade_Delete_Attachments()
    {
        Skip.IfNot(SkipReason is null, SkipReason);
        var db = Database;
        var store = new DocumentStore(db.Client, db.DatabaseNamespace.DatabaseName);

        var entity = new SimpleEntity { Id = 100, Name = "Parent" };
        var collectionName = store.Conventions.GetCollectionName(typeof(SimpleEntity));
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

    [SkippableFact]
    public async Task Session_Should_Rollback_Attachments()
    {
        Skip.IfNot(SkipReason is null, SkipReason);
        var db = Database;
        var store = new DocumentStore(db.Client, db.DatabaseNamespace.DatabaseName);

        var documentId = "tx/1";
        var attachmentName = "secret.txt";

        try
        {
            using var session = store.OpenSession();
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes("shhh"));

            await session.Attachments.StoreAsync(documentId, attachmentName, stream);

            // Bail out before SaveChangesAsync: disposing the session aborts the transaction that
            // the attachment write opened, so nothing may reach the database.
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
