using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace MongoZen;

internal class InMemoryFileData
{
    public byte[] Content { get; set; } = null!;
    public string ContentType { get; set; } = null!;
}

internal class InMemoryAttachmentsSession : IAttachmentsSession
{
    // Shared state for all sessions of the same context
    private readonly ConcurrentDictionary<string, InMemoryFileData> _files;

    public InMemoryAttachmentsSession(ConcurrentDictionary<string, InMemoryFileData> files)
    {
        _files = files;
    }

    private string GetKey(object entityId, string name) => $"{entityId}/{name}";

    public Task StoreAsync(object entityId, string name, Stream stream, string? contentType = null, CancellationToken ct = default)
    {
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        _files[GetKey(entityId, name)] = new InMemoryFileData
        {
            Content = ms.ToArray(),
            ContentType = contentType ?? "application/octet-stream"
        };
        return Task.CompletedTask;
    }

    public Task<Stream> GetAsync(object entityId, string name, CancellationToken ct = default)
    {
        if (_files.TryGetValue(GetKey(entityId, name), out var data))
        {
            return Task.FromResult<Stream>(new MemoryStream(data.Content));
        }
        throw new FileNotFoundException($"Attachment '{name}' for entity '{entityId}' not found.");
    }

    public Task DeleteAsync(object entityId, string name, CancellationToken ct = default)
    {
        _files.TryRemove(GetKey(entityId, name), out _);
        return Task.CompletedTask;
    }

    public Task<IEnumerable<AttachmentName>> GetNamesAsync(object entityId, CancellationToken ct = default)
    {
        var prefix = $"{entityId}/";
        var results = _files
            .Where(x => x.Key.StartsWith(prefix))
            .Select(x =>
            {
                var name = x.Key.Substring(prefix.Length);
                return new AttachmentName(name, x.Value.ContentType, x.Value.Content.Length);
            })
            .AsEnumerable();

        return Task.FromResult(results);
    }
}
