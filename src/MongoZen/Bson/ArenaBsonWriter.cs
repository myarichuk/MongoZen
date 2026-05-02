using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using SharpArena.Allocators;
using SharpArena.Collections;
using MongoDB.Bson;

namespace MongoZen.Bson;

/// <summary>
/// A high-performance, zero-allocation BSON builder that writes directly into an arena.
/// </summary>
public unsafe struct ArenaBsonWriter(ArenaAllocator arena, int initialCapacity = 256)
{
    private readonly ArenaAllocator _arena = arena;
    private ArenaList<byte> _buffer = new(arena, initialCapacity);
    private ArenaList<int> _lengthOffsets = new(arena, 8);

    public int Position => _buffer.Length;

    /// <summary>
    /// Seals the document and returns a navigatable BlittableBsonDocument.
    /// </summary>
    public BlittableBsonDocument Commit(ArenaAllocator arena)
    {
        if (_lengthOffsets.Length != 0)
        {
            throw new InvalidOperationException("Not all documents/arrays were closed. Depth: " + _lengthOffsets.Length);
        }
        
        // Ensure we are contiguous. ArenaList.AsPtr is the start of the contiguous block.
        // BlittableBsonDocument expects a scan to build its index.
        return ArenaBsonReader.ReadInPlace(_buffer.AsPtr, _buffer.Length, arena);
    }

    public void WriteStartDocument()
    {
        _lengthOffsets.Add(_buffer.Length);
        // Reserve 4 bytes for length
        _buffer.Add(0); _buffer.Add(0); _buffer.Add(0); _buffer.Add(0);
    }

    public void WriteEndDocument()
    {
        if (_lengthOffsets.Length == 0) throw new InvalidOperationException("No open document/array.");
        
        _buffer.Add(BlittableBsonConstants.DocumentTerminator);
        
        int offsetIndex = _lengthOffsets.Length - 1;
        int startPos = _lengthOffsets[offsetIndex];
        _lengthOffsets.RemoveAt(offsetIndex);

        int totalLength = _buffer.Length - startPos;
        
        // Patch the length at the start position
        byte* pLen = _buffer.AsPtr + startPos;
        *(int*)pLen = totalLength;
    }

    public void WriteStartArray() => WriteStartDocument(); // In BSON, arrays are just documents with keys "0", "1"...
    public void WriteEndArray() => WriteEndDocument();

    public void WriteName(ReadOnlySpan<char> name, BlittableBsonConstants.BsonType type)
    {
        _buffer.Add((byte)type);
        
        int byteCount = Encoding.UTF8.GetByteCount(name);
        // Field names are usually small; 128 bytes covers almost all practical BSON keys
        if (byteCount <= 128)
        {
            Span<byte> nameBytes = stackalloc byte[128];
            int written = Encoding.UTF8.GetBytes(name, nameBytes);
            _buffer.AddRange(nameBytes.Slice(0, written));
        }
        else
        {
            byte[] rented = ArrayPool<byte>.Shared.Rent(byteCount);
            try
            {
                int written = Encoding.UTF8.GetBytes(name, rented);
                _buffer.AddRange(new ReadOnlySpan<byte>(rented, 0, written));
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }
        
        _buffer.Add(0); // C-string null terminator
    }

    public void WriteInt32(ReadOnlySpan<char> name, int value)
    {
        WriteName(name, BlittableBsonConstants.BsonType.Int32);
        WriteInt32Value(value);
    }

    public void WriteInt64(ReadOnlySpan<char> name, long value)
    {
        WriteName(name, BlittableBsonConstants.BsonType.Int64);
        WriteInt64Value(value);
    }

    public void WriteDouble(ReadOnlySpan<char> name, double value)
    {
        WriteName(name, BlittableBsonConstants.BsonType.Double);
        WriteDoubleValue(value);
    }

    public void WriteBoolean(ReadOnlySpan<char> name, bool value)
    {
        WriteName(name, BlittableBsonConstants.BsonType.Boolean);
        _buffer.Add(value ? (byte)1 : (byte)0);
    }

    public void WriteString(ReadOnlySpan<char> name, ReadOnlySpan<char> value)
    {
        WriteName(name, BlittableBsonConstants.BsonType.String);
        
        int byteCount = Encoding.UTF8.GetByteCount(value);
        
        // BSON string length includes null terminator
        WriteInt32Value(byteCount + 1);

        if (byteCount <= 512)
        {
            Span<byte> valBytes = stackalloc byte[512];
            int written = Encoding.UTF8.GetBytes(value, valBytes);
            _buffer.AddRange(valBytes.Slice(0, written));
        }
        else
        {
            byte[] rented = ArrayPool<byte>.Shared.Rent(byteCount);
            try
            {
                int written = Encoding.UTF8.GetBytes(value, rented);
                _buffer.AddRange(new ReadOnlySpan<byte>(rented, 0, written));
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }

        _buffer.Add(0);
    }

    public void WriteObjectId(ReadOnlySpan<char> name, ObjectId value)
    {
        WriteName(name, BlittableBsonConstants.BsonType.ObjectId);
        
        // Use ArrayPool to avoid heap allocation while ensuring correct endianness via the driver
        byte[] bytes = ArrayPool<byte>.Shared.Rent(12);
        try
        {
            value.ToByteArray(bytes, 0);
            _buffer.AddRange(new ReadOnlySpan<byte>(bytes, 0, 12));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(bytes);
        }
    }

    public void WriteDateTime(ReadOnlySpan<char> name, DateTime value)
    {
        WriteName(name, BlittableBsonConstants.BsonType.DateTime);
        long ms = (long)(value.ToUniversalTime() - DateTime.UnixEpoch).TotalMilliseconds;
        WriteInt64Value(ms);
    }

    public void WriteNull(ReadOnlySpan<char> name)
    {
        WriteName(name, BlittableBsonConstants.BsonType.Null);
    }

    public void WriteStartDocument(ReadOnlySpan<char> name)
    {
        WriteName(name, BlittableBsonConstants.BsonType.Document);
        WriteStartDocument();
    }

    public void WriteStartArray(ReadOnlySpan<char> name)
    {
        WriteName(name, BlittableBsonConstants.BsonType.Array);
        WriteStartArray();
    }

    // Value-only versions for arrays (using index as string key)
    public void WriteInt32(int index, int value)
    {
        Span<char> name = stackalloc char[11];
        index.TryFormat(name, out int charsWritten);
        WriteInt32(name.Slice(0, charsWritten), value);
    }

    public void WriteString(int index, ReadOnlySpan<char> value)
    {
        Span<char> name = stackalloc char[11];
        index.TryFormat(name, out int charsWritten);
        WriteString(name.Slice(0, charsWritten), value);
    }

    private void WriteInt32Value(int value)
    {
        Span<byte> bytes = stackalloc byte[4];
        Unsafe.WriteUnaligned(ref MemoryMarshal.GetReference(bytes), value);
        _buffer.AddRange(bytes);
    }

    private void WriteInt64Value(long value)
    {
        Span<byte> bytes = stackalloc byte[8];
        Unsafe.WriteUnaligned(ref MemoryMarshal.GetReference(bytes), value);
        _buffer.AddRange(bytes);
    }

    private void WriteDoubleValue(double value)
    {
        Span<byte> bytes = stackalloc byte[8];
        Unsafe.WriteUnaligned(ref MemoryMarshal.GetReference(bytes), value);
        _buffer.AddRange(bytes);
    }
}
