using System.Buffers;
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

        // 1. Write chunks using pooled buffer
        byte[] buffer = ArrayPool<byte>.Shared.Rent(ChunkSize);
        try
        {
            int bytesRead;
            int chunkIndex = 0;
            long totalLength = 0;
            var chunks = new List<BsonDocument>();

            while ((bytesRead = await stream.ReadAsync(buffer, 0, ChunkSize, cancellationToken)) > 0)
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

                chunks.Add(chunkDoc);

                chunkIndex++;
                totalLength += bytesRead;

                if (chunks.Count >= 4) // Batch of ~1MB
                {
                    if (_session.ClientSession != null)
                    {
                        await ChunksCollection.InsertManyAsync(_session.ClientSession, chunks, null, cancellationToken);
                    }
                    else
                    {
                        await ChunksCollection.InsertManyAsync(chunks, null, cancellationToken);
                    }

                    chunks.Clear();
                }
            }

            if (chunks.Count > 0)
            {
                if (_session.ClientSession != null)
                {
                    await ChunksCollection.InsertManyAsync(_session.ClientSession, chunks, null, cancellationToken);
                }
                else
                {
                    await ChunksCollection.InsertManyAsync(chunks, null, cancellationToken);
                }
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
                        { "documentId", _session.Conventions.CreateBsonValue(documentId) },
                        { "contentType", contentType ?? "application/octet-stream" }
                    }
                }
            };

            if (_session.ClientSession != null)
            {
                await FilesCollection.InsertOneAsync(_session.ClientSession, fileDoc, null, cancellationToken);
            }
            else
            {
                await FilesCollection.InsertOneAsync(fileDoc, null, cancellationToken);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public async Task<AttachmentResult> GetAsync(object documentId, string name, CancellationToken cancellationToken = default)
    {
        await _session.EnsureTransactionStartedAsync(cancellationToken);

        var filter = Builders<BsonDocument>.Filter.And(
            Builders<BsonDocument>.Filter.Eq("filename", name),
            Builders<BsonDocument>.Filter.Eq("metadata.documentId", _session.Conventions.CreateBsonValue(documentId))
        );

        var cursor = _session.ClientSession != null
            ? await FilesCollection.Find(_session.ClientSession, filter).FirstOrDefaultAsync(cancellationToken)
            : await FilesCollection.Find(filter).FirstOrDefaultAsync(cancellationToken);

        var fileDoc = cursor;
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
            Builders<BsonDocument>.Filter.Eq("metadata.documentId", _session.Conventions.CreateBsonValue(documentId))
        );

        var fileDoc = _session.ClientSession != null
            ? await FilesCollection.Find(_session.ClientSession, filter).FirstOrDefaultAsync(cancellationToken)
            : await FilesCollection.Find(filter).FirstOrDefaultAsync(cancellationToken);

        if (fileDoc != null)
        {
            var filesId = fileDoc["_id"];
            if (_session.ClientSession != null)
            {
                await FilesCollection.DeleteOneAsync(_session.ClientSession, Builders<BsonDocument>.Filter.Eq("_id", filesId), null, cancellationToken);
                await ChunksCollection.DeleteManyAsync(_session.ClientSession, Builders<BsonDocument>.Filter.Eq("files_id", filesId), null, cancellationToken);
            }
            else
            {
                await FilesCollection.DeleteOneAsync(Builders<BsonDocument>.Filter.Eq("_id", filesId), cancellationToken);
                await ChunksCollection.DeleteManyAsync(Builders<BsonDocument>.Filter.Eq("files_id", filesId), cancellationToken);
            }
        }
    }

    public async Task DeleteAllAsync(object documentId, CancellationToken cancellationToken = default)
    {
        await _session.EnsureTransactionStartedAsync(cancellationToken);

        var filter = Builders<BsonDocument>.Filter.Eq("metadata.documentId", _session.Conventions.CreateBsonValue(documentId));
        var files = _session.ClientSession != null
            ? await FilesCollection.Find(_session.ClientSession, filter).ToListAsync(cancellationToken)
            : await FilesCollection.Find(filter).ToListAsync(cancellationToken);
        
        if (files.Count == 0)
        {
            return;
        }

        var fileIds = files.Select(f => f["_id"]).ToList();
        
        // Batch delete both files and chunks
        if (_session.ClientSession != null)
        {
            await FilesCollection.DeleteManyAsync(_session.ClientSession, Builders<BsonDocument>.Filter.In("_id", fileIds), null, cancellationToken);
            await ChunksCollection.DeleteManyAsync(_session.ClientSession, Builders<BsonDocument>.Filter.In("files_id", fileIds), null, cancellationToken);
        }
        else
        {
            await FilesCollection.DeleteManyAsync(Builders<BsonDocument>.Filter.In("_id", fileIds), cancellationToken);
            await ChunksCollection.DeleteManyAsync(Builders<BsonDocument>.Filter.In("files_id", fileIds), cancellationToken);
        }
    }

    public async Task<IEnumerable<string>> GetNamesAsync(object documentId, CancellationToken cancellationToken = default)
    {
        await _session.EnsureTransactionStartedAsync(cancellationToken);

        var filter = Builders<BsonDocument>.Filter.Eq("metadata.documentId", _session.Conventions.CreateBsonValue(documentId));
        var cursor = _session.ClientSession != null
            ? await FilesCollection.Find(_session.ClientSession, filter).ToListAsync(cancellationToken)
            : await FilesCollection.Find(filter).ToListAsync(cancellationToken);
            
        return cursor.Select(doc => doc["filename"].AsString);
    }
}
