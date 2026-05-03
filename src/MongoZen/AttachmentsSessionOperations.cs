using MongoDB.Driver;
using MongoDB.Bson;
using MongoZen.Bson;

namespace MongoZen;

internal sealed class AttachmentsSessionOperations(DocumentSession session) : IAttachmentsSessionOperations
{
    private readonly DocumentSession _session = session;
    private IMongoCollection<BsonDocument> FilesCollection => _session.Database.GetCollection<BsonDocument>("fs.files");
    private IMongoCollection<BsonDocument> ChunksCollection => _session.Database.GetCollection<BsonDocument>("fs.chunks");
    private const int ChunkSize = 261120; // 255KB

    public async Task StoreAsync(object documentId, string name, Stream stream, string? contentType = null, CancellationToken cancellationToken = default)
    {
        await _session.EnsureTransactionStartedAsync(cancellationToken);
        var filesId = ObjectId.GenerateNewId();

        // 1. Write chunks
        byte[] buffer = new byte[ChunkSize];
        int bytesRead;
        int chunkIndex = 0;
        long totalLength = 0;

        while ((bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length, cancellationToken)) > 0)
        {
            var chunkData = new byte[bytesRead];
            Buffer.BlockCopy(buffer, 0, chunkData, 0, bytesRead);

            var chunkDoc = new BsonDocument
            {
                { "_id", ObjectId.GenerateNewId() },
                { "files_id", filesId },
                { "n", chunkIndex },
                { "data", new BsonBinaryData(chunkData) }
            };

            await ChunksCollection.InsertOneAsync(_session.ClientSession, chunkDoc, null, cancellationToken);

            chunkIndex++;
            totalLength += bytesRead;
        }

        // 2. Write files metadata
        var fileDoc = new BsonDocument
        {
            { "_id", filesId },
            { "length", totalLength },
            { "chunkSize", ChunkSize },
            { "uploadDate", DateTime.UtcNow },
            { "filename", name },
            { "metadata", new BsonDocument
                {
                    { "documentId", BsonValue.Create(documentId) },
                    { "contentType", contentType ?? "application/octet-stream" }
                }
            }
        };

        await FilesCollection.InsertOneAsync(_session.ClientSession, fileDoc, null, cancellationToken);
    }

    public async Task<AttachmentResult> GetAsync(object documentId, string name, CancellationToken cancellationToken = default)
    {
        await _session.EnsureTransactionStartedAsync(cancellationToken);

        var filter = Builders<BsonDocument>.Filter.And(
            Builders<BsonDocument>.Filter.Eq("filename", name),
            Builders<BsonDocument>.Filter.Eq("metadata.documentId", BsonValue.Create(documentId))
        );

        var fileDoc = await FilesCollection.Find(_session.ClientSession, filter).FirstOrDefaultAsync(cancellationToken);
        if (fileDoc == null)
        {
            throw new KeyNotFoundException($"Attachment '{name}' not found for document '{documentId}'.");
        }

        var filesId = fileDoc["_id"];
        var length = fileDoc["length"].AsInt64;
        var chunkSize = fileDoc["chunkSize"].AsInt32;
        var cType = fileDoc["metadata"].AsBsonDocument.GetValue("contentType", "application/octet-stream").AsString;

        var stream = new BlittableGridFSDownloadStream(ChunksCollection, filesId, _session.ClientSession, length, chunkSize);
        return new AttachmentResult(name, stream, cType);
    }

    public async Task DeleteAsync(object documentId, string name, CancellationToken cancellationToken = default)
    {
        await _session.EnsureTransactionStartedAsync(cancellationToken);

        var filter = Builders<BsonDocument>.Filter.And(
            Builders<BsonDocument>.Filter.Eq("filename", name),
            Builders<BsonDocument>.Filter.Eq("metadata.documentId", BsonValue.Create(documentId))
        );

        var fileDoc = await FilesCollection.Find(_session.ClientSession, filter).FirstOrDefaultAsync(cancellationToken);
        if (fileDoc != null)
        {
            var filesId = fileDoc["_id"];
            await FilesCollection.DeleteOneAsync(_session.ClientSession, Builders<BsonDocument>.Filter.Eq("_id", filesId), null, cancellationToken);
            await ChunksCollection.DeleteManyAsync(_session.ClientSession, Builders<BsonDocument>.Filter.Eq("files_id", filesId), null, cancellationToken);
        }
    }

    public async Task DeleteAllAsync(object documentId, CancellationToken cancellationToken = default)
    {
        await _session.EnsureTransactionStartedAsync(cancellationToken);

        var filter = Builders<BsonDocument>.Filter.Eq("metadata.documentId", BsonValue.Create(documentId));
        var cursor = await FilesCollection.Find(_session.ClientSession, filter).ToListAsync(cancellationToken);
        
        foreach (var fileDoc in cursor)
        {
            var filesId = fileDoc["_id"];
            await FilesCollection.DeleteOneAsync(_session.ClientSession, Builders<BsonDocument>.Filter.Eq("_id", filesId), null, cancellationToken);
            await ChunksCollection.DeleteManyAsync(_session.ClientSession, Builders<BsonDocument>.Filter.Eq("files_id", filesId), null, cancellationToken);
        }
    }

    public async Task<IEnumerable<string>> GetNamesAsync(object documentId, CancellationToken cancellationToken = default)
    {
        await _session.EnsureTransactionStartedAsync(cancellationToken);

        var filter = Builders<BsonDocument>.Filter.Eq("metadata.documentId", BsonValue.Create(documentId));
        var cursor = await FilesCollection.Find(_session.ClientSession, filter).ToListAsync(cancellationToken);
        return cursor.Select(doc => doc["filename"].AsString);
    }
}
