using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MongoDB.Bson;
using MongoDB.Driver;

namespace MongoZen.Bson;

internal sealed class BlittableGridFSDownloadStream : Stream
{
    private readonly IMongoCollection<BsonDocument> _chunks;
    private readonly BsonValue _filesId;
    private readonly IClientSessionHandle? _session;
    private readonly long _length;
    private readonly int _chunkSize;
    private long _position;
    private int _currentChunkIndex = -1;
    private byte[]? _currentChunkData;

    public BlittableGridFSDownloadStream(
        IMongoCollection<BsonDocument> chunks,
        BsonValue filesId,
        IClientSessionHandle? session,
        long length,
        int chunkSize)
    {
        _chunks = chunks;
        _filesId = filesId;
        _session = session;
        _length = length;
        _chunkSize = chunkSize;
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => _length;
    public override long Position
    {
        get => _position;
        set => throw new NotSupportedException();
    }

    public override void Flush() { }

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (_position >= _length)
        {
            return 0;
        }

        int chunkIndex = (int)(_position / _chunkSize);
        int chunkOffset = (int)(_position % _chunkSize);

        if (_currentChunkIndex != chunkIndex)
        {
            LoadChunk(chunkIndex);
        }

        if (_currentChunkData == null)
        {
            return 0;
        }

        int available = _currentChunkData.Length - chunkOffset;
        int toCopy = Math.Min(count, available);
        
        Buffer.BlockCopy(_currentChunkData, chunkOffset, buffer, offset, toCopy);
        
        _position += toCopy;
        return toCopy;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_position >= _length)
        {
            return 0;
        }

        int chunkIndex = (int)(_position / _chunkSize);
        int chunkOffset = (int)(_position % _chunkSize);

        if (_currentChunkIndex != chunkIndex)
        {
            await LoadChunkAsync(chunkIndex, cancellationToken);
        }

        if (_currentChunkData == null)
        {
            return 0;
        }

        int available = _currentChunkData.Length - chunkOffset;
        int toCopy = Math.Min(buffer.Length, available);
        
        _currentChunkData.AsMemory(chunkOffset, toCopy).CopyTo(buffer);
        
        _position += toCopy;
        return toCopy;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        return ReadAsync(new Memory<byte>(buffer, offset, count), cancellationToken).AsTask();
    }

    private void LoadChunk(int chunkIndex)
    {
        var filter = Builders<BsonDocument>.Filter.And(
            Builders<BsonDocument>.Filter.Eq("files_id", _filesId),
            Builders<BsonDocument>.Filter.Eq("n", chunkIndex)
        );

        BsonDocument? chunkDoc;
        if (_session != null)
        {
            chunkDoc = _chunks.Find(_session, filter).FirstOrDefault();
        }
        else
        {
            chunkDoc = _chunks.Find(filter).FirstOrDefault();
        }

        if (chunkDoc == null)
        {
            throw new EndOfStreamException($"Chunk {chunkIndex} for file {_filesId} not found.");
        }

        _currentChunkData = chunkDoc["data"].AsByteArray;
        _currentChunkIndex = chunkIndex;
    }

    private async ValueTask LoadChunkAsync(int chunkIndex, CancellationToken cancellationToken)
    {
        var filter = Builders<BsonDocument>.Filter.And(
            Builders<BsonDocument>.Filter.Eq("files_id", _filesId),
            Builders<BsonDocument>.Filter.Eq("n", chunkIndex)
        );

        BsonDocument? chunkDoc;
        if (_session != null)
        {
            chunkDoc = await _chunks.Find(_session, filter).FirstOrDefaultAsync(cancellationToken);
        }
        else
        {
            chunkDoc = await _chunks.Find(filter).FirstOrDefaultAsync(cancellationToken);
        }

        if (chunkDoc == null)
        {
            throw new EndOfStreamException($"Chunk {chunkIndex} for file {_filesId} not found.");
        }

        _currentChunkData = chunkDoc["data"].AsByteArray;
        _currentChunkIndex = chunkIndex;
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
