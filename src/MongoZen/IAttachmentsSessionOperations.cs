using MongoDB.Driver.GridFS;
using MongoDB.Bson;

namespace MongoZen;

/// <summary>
/// Attachment operations for a session. Writes become durable when the session's next
/// <see cref="IDocumentSession.SaveChangesAsync"/> commits (on deployments with transaction
/// support they are rolled back if the session is disposed first); reads do not start a transaction.
/// </summary>
public interface IAttachmentsSessionOperations
{
    ValueTask StoreAsync(object documentId, string name, Stream stream, string? contentType = null, CancellationToken cancellationToken = default);
    ValueTask<AttachmentResult> GetAsync(object documentId, string name, CancellationToken cancellationToken = default);
    ValueTask DeleteAsync(object documentId, string name, CancellationToken cancellationToken = default);
    ValueTask DeleteAllAsync(object documentId, CancellationToken cancellationToken = default);
    ValueTask<IEnumerable<string>> GetNamesAsync(object documentId, CancellationToken cancellationToken = default);
}

public sealed class AttachmentResult : IDisposable, IAsyncDisposable
{
    public string Name { get; }
    public Stream Stream { get; }
    public string? ContentType { get; }
    public long Length => Stream.Length;

    public AttachmentResult(string name, Stream stream, string? contentType)
    {
        Name = name;
        Stream = stream;
        ContentType = contentType;
    }

    public void Dispose() => Stream.Dispose();

    public ValueTask DisposeAsync() => Stream.DisposeAsync();
}
