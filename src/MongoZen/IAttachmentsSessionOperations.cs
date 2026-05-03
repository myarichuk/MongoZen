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

public sealed class AttachmentResult(string name, Stream stream, string? contentType) : IDisposable
{
    public string Name { get; } = name;
    public Stream Stream { get; } = stream;
    public string? ContentType { get; } = contentType;
    public long Length => stream.Length;

    public void Dispose() => Stream.Dispose();
}
