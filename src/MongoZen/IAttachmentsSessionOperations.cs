using MongoDB.Driver.GridFS;
using MongoDB.Bson;

namespace MongoZen;

public interface IAttachmentsSessionOperations
{
    Task StoreAsync(object documentId, string name, Stream stream, string? contentType = null, CancellationToken cancellationToken = default);
    Task<AttachmentResult> GetAsync(object documentId, string name, CancellationToken cancellationToken = default);
    Task DeleteAsync(object documentId, string name, CancellationToken cancellationToken = default);
    Task DeleteAllAsync(object documentId, CancellationToken cancellationToken = default);
    Task<IEnumerable<string>> GetNamesAsync(object documentId, CancellationToken cancellationToken = default);
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
