using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace MongoZen;

/// <summary>
/// Represents metadata for an attachment.
/// </summary>
public record AttachmentName(string Name, string ContentType, long Size);

/// <summary>
/// Provides an API for managing file attachments linked to entities using GridFS.
/// </summary>
public interface IAttachmentsSession
{
    /// <summary>
    /// Stores an attachment for the specified entity.
    /// </summary>
    Task StoreAsync(object entityId, string name, Stream stream, string? contentType = null, CancellationToken ct = default);

    /// <summary>
    /// Opens a stream to download an attachment.
    /// </summary>
    Task<Stream> GetAsync(object entityId, string name, CancellationToken ct = default);

    /// <summary>
    /// Deletes an attachment.
    /// </summary>
    Task DeleteAsync(object entityId, string name, CancellationToken ct = default);

    /// <summary>
    /// Gets the names and metadata of all attachments for the specified entity.
    /// </summary>
    Task<IEnumerable<AttachmentName>> GetNamesAsync(object entityId, CancellationToken ct = default);
}
