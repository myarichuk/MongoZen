using MongoDB.Driver;
using MongoDB.Driver.GridFS;
using MongoDB.Bson;

namespace MongoZen;

internal sealed class AttachmentsSessionOperations(IMongoDatabase database, IClientSessionHandle? session) : IAttachmentsSessionOperations
{
    private readonly IGridFSBucket _bucket = new GridFSBucket(database);

    public async Task StoreAsync(object documentId, string name, Stream stream, string? contentType = null, CancellationToken cancellationToken = default)
    {
        var options = new GridFSUploadOptions
        {
            Metadata = new BsonDocument
            {
                { "documentId", BsonValue.Create(documentId) },
                { "contentType", contentType ?? "application/octet-stream" }
            }
        };

        // TODO: Use session overload once available in driver version
        await _bucket.UploadFromStreamAsync(name, stream, options, cancellationToken);
    }

    public async Task<AttachmentResult> GetAsync(object documentId, string name, CancellationToken cancellationToken = default)
    {
        var filter = Builders<GridFSFileInfo>.Filter.And(
            Builders<GridFSFileInfo>.Filter.Eq(x => x.Filename, name),
            Builders<GridFSFileInfo>.Filter.Eq("metadata.documentId", BsonValue.Create(documentId))
        );

        var cursor = await _bucket.FindAsync(filter, null, cancellationToken);
        var fileInfo = await cursor.FirstOrDefaultAsync(cancellationToken);
        if (fileInfo == null)
        {
            throw new KeyNotFoundException($"Attachment '{name}' not found for document '{documentId}'.");
        }

        var stream = await _bucket.OpenDownloadStreamAsync(fileInfo.Id, null, cancellationToken);
        return new AttachmentResult(name, (GridFSDownloadStream<ObjectId>)stream, fileInfo.Metadata.GetValue("contentType", "application/octet-stream").AsString);
    }

    public async Task DeleteAsync(object documentId, string name, CancellationToken cancellationToken = default)
    {
        var filter = Builders<GridFSFileInfo>.Filter.And(
            Builders<GridFSFileInfo>.Filter.Eq(x => x.Filename, name),
            Builders<GridFSFileInfo>.Filter.Eq("metadata.documentId", BsonValue.Create(documentId))
        );

        var cursor = await _bucket.FindAsync(filter, null, cancellationToken);
        var fileInfo = await cursor.FirstOrDefaultAsync(cancellationToken);
        if (fileInfo != null)
        {
            await _bucket.DeleteAsync(fileInfo.Id, cancellationToken);
        }
    }

    public async Task DeleteAllAsync(object documentId, CancellationToken cancellationToken = default)
    {
        var filter = Builders<GridFSFileInfo>.Filter.Eq("metadata.documentId", BsonValue.Create(documentId));
        var cursor = await _bucket.FindAsync(filter, null, cancellationToken);
        var files = await cursor.ToListAsync(cancellationToken);
        
        foreach (var file in files)
        {
            await _bucket.DeleteAsync(file.Id, cancellationToken);
        }
    }

    public async Task<IEnumerable<string>> GetNamesAsync(object documentId, CancellationToken cancellationToken = default)
    {
        var filter = Builders<GridFSFileInfo>.Filter.Eq("metadata.documentId", BsonValue.Create(documentId));
        var cursor = await _bucket.FindAsync(filter, null, cancellationToken);
        var files = await cursor.ToListAsync(cancellationToken);
        return files.Select(x => x.Filename);
    }
}
