using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MongoDB.Bson;
using MongoDB.Driver;

namespace MongoZen;

internal class GridFSAttachmentsSession : IAttachmentsSession
{
    private readonly IMongoCollection<BsonDocument> _filesCollection;
    private readonly IMongoCollection<BsonDocument> _chunksCollection;
    private readonly IClientSessionHandle? _session;
    private const int ChunkSize = 255 * 1024; // 255KB standard GridFS chunk size

    public GridFSAttachmentsSession(IMongoDatabase database, string bucketName, IClientSessionHandle? session)
    {
        _filesCollection = database.GetCollection<BsonDocument>(bucketName + ".files");
        _chunksCollection = database.GetCollection<BsonDocument>(bucketName + ".chunks");
        _session = session;
    }

    private string GetGridFSFileName(object entityId, string name) => $"{entityId}/{name}";

    public async Task StoreAsync(object entityId, string name, Stream stream, string? contentType = null, CancellationToken ct = default)
    {
        var filename = GetGridFSFileName(entityId, name);
        var fileId = ObjectId.GenerateNewId();
        
        await DeleteAsync(entityId, name, ct);

        // 1. Upload Chunks in batches
        var pool = ArrayPool<byte>.Shared;
        var buffer = pool.Rent(ChunkSize);
        var batch = new List<BsonDocument>(16);
        try
        {
            int chunkIndex = 0;
            long totalLength = 0;
            int bytesRead;

            while ((bytesRead = await stream.ReadAsync(buffer, 0, ChunkSize, ct)) > 0)
            {
                var dataToUpload = new byte[bytesRead];
                Buffer.BlockCopy(buffer, 0, dataToUpload, 0, bytesRead);

                batch.Add(new BsonDocument
                {
                    { "_id", ObjectId.GenerateNewId() },
                    { "files_id", fileId },
                    { "n", chunkIndex++ },
                    { "data", new BsonBinaryData(dataToUpload) }
                });

                if (batch.Count >= 16)
                {
                    if (_session != null) await _chunksCollection.InsertManyAsync(_session, batch, null, ct);
                    else await _chunksCollection.InsertManyAsync(batch, null, ct);
                    batch.Clear();
                }

                totalLength += bytesRead;
            }

            if (batch.Count > 0)
            {
                if (_session != null) await _chunksCollection.InsertManyAsync(_session, batch, null, ct);
                else await _chunksCollection.InsertManyAsync(batch, null, ct);
            }

            // 2. Upload File Metadata
            var fileDoc = new BsonDocument
            {
                { "_id", fileId },
                { "length", totalLength },
                { "chunkSize", ChunkSize },
                { "uploadDate", DateTime.UtcNow },
                { "filename", filename },
                { "metadata", new BsonDocument 
                    { 
                        { "EntityId", BsonValue.Create(entityId) },
                        { "Name", name },
                        { "ContentType", contentType ?? "application/octet-stream" }
                    } 
                }
            };

            if (_session != null) await _filesCollection.InsertOneAsync(_session, fileDoc, null, ct);
            else await _filesCollection.InsertOneAsync(fileDoc, null, ct);
        }
        finally
        {
            pool.Return(buffer);
        }
    }

    public async Task<Stream> GetAsync(object entityId, string name, CancellationToken ct = default)
    {
        var filename = GetGridFSFileName(entityId, name);
        var filter = Builders<BsonDocument>.Filter.Eq("filename", filename);
        
        BsonDocument fileDoc;
        if (_session != null) fileDoc = await _filesCollection.Find(_session, filter).FirstOrDefaultAsync(ct);
        else fileDoc = await _filesCollection.Find(filter).FirstOrDefaultAsync(ct);

        if (fileDoc == null) throw new FileNotFoundException($"Attachment '{name}' not found for entity '{entityId}'.");

        return new GridFSDownloadStream(_chunksCollection, _session, fileDoc["_id"], fileDoc["length"].ToInt64());
    }

    public async Task DeleteAsync(object entityId, string name, CancellationToken ct = default)
    {
        var filename = GetGridFSFileName(entityId, name);
        var filter = Builders<BsonDocument>.Filter.Eq("filename", filename);

        using var cursor = _session != null
            ? await _filesCollection.Find(_session, filter).ToCursorAsync(ct)
            : await _filesCollection.Find(filter).ToCursorAsync(ct);

        while (await cursor.MoveNextAsync(ct))
        {
            foreach (var file in cursor.Current)
            {
                var fileId = file["_id"];
                var chunksFilter = Builders<BsonDocument>.Filter.Eq("files_id", fileId);

                if (_session != null)
                {
                    await _chunksCollection.DeleteManyAsync(_session, chunksFilter, null, ct);
                    await _filesCollection.DeleteOneAsync(_session, Builders<BsonDocument>.Filter.Eq("_id", fileId), null, ct);
                }
                else
                {
                    await _chunksCollection.DeleteManyAsync(chunksFilter, null, ct);
                    await _filesCollection.DeleteOneAsync(Builders<BsonDocument>.Filter.Eq("_id", fileId), null, ct);
                }
            }
        }
    }

    public async Task<IEnumerable<AttachmentName>> GetNamesAsync(object entityId, CancellationToken ct = default)
    {
        var filter = Builders<BsonDocument>.Filter.Eq("metadata.EntityId", BsonValue.Create(entityId));
        
        var results = new List<AttachmentName>();
        using var cursor = _session != null
            ? await _filesCollection.Find(_session, filter).ToCursorAsync(ct)
            : await _filesCollection.Find(filter).ToCursorAsync(ct);

        while (await cursor.MoveNextAsync(ct))
        {
            foreach (var file in cursor.Current)
            {
                var metadata = file.GetValue("metadata", new BsonDocument()).AsBsonDocument;
                var name = metadata.GetValue("Name", file["filename"]).AsString;
                var contentType = metadata.GetValue("ContentType", "application/octet-stream").AsString;
                results.Add(new AttachmentName(name, contentType, file["length"].ToInt64()));
            }
        }

        return results;
    }

    private sealed class GridFSDownloadStream : Stream
    {
        private readonly IMongoCollection<BsonDocument> _chunks;
        private readonly IClientSessionHandle? _session;
        private readonly BsonValue _fileId;
        private readonly long _length;
        private long _position;
        private byte[]? _currentChunkData;
        private int _currentChunkIndex = -1;

        public GridFSDownloadStream(IMongoCollection<BsonDocument> chunks, IClientSessionHandle? session, BsonValue fileId, long length)
        {
            _chunks = chunks;
            _session = session;
            _fileId = fileId;
            _length = length;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _length;
        public override long Position { get => _position; set => throw new NotSupportedException(); }

        public override void Flush() { }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            if (_position >= _length) return 0;

            int totalRead = 0;
            while (count > 0 && _position < _length)
            {
                int chunkIndex = (int)(_position / ChunkSize);
                if (_currentChunkIndex != chunkIndex)
                {
                    var filter = Builders<BsonDocument>.Filter.And(
                        Builders<BsonDocument>.Filter.Eq("files_id", _fileId),
                        Builders<BsonDocument>.Filter.Eq("n", chunkIndex)
                    );

                    BsonDocument chunk;
                    if (_session != null) chunk = await _chunks.Find(_session, filter).FirstOrDefaultAsync(cancellationToken);
                    else chunk = await _chunks.Find(filter).FirstOrDefaultAsync(cancellationToken);

                    if (chunk == null) throw new InvalidDataException($"Missing GridFS chunk {chunkIndex} for file {_fileId}");
                    
                    _currentChunkData = chunk["data"].AsBsonBinaryData.Bytes;
                    _currentChunkIndex = chunkIndex;
                }

                int chunkOffset = (int)(_position % ChunkSize);
                int available = _currentChunkData!.Length - chunkOffset;
                int toCopy = Math.Min(available, count);

                Buffer.BlockCopy(_currentChunkData, chunkOffset, buffer, offset, toCopy);

                _position += toCopy;
                offset += toCopy;
                count -= toCopy;
                totalRead += toCopy;
            }

            return totalRead;
        }

        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer, offset, count, default).GetAwaiter().GetResult();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
