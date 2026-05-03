using System;
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

    private static readonly string[] IndexStrings = GenerateIndexStrings();
    private static string[] GenerateIndexStrings()
    {
        var strings = new string[100];
        for (int i = 0; i < 100; i++) strings[i] = i.ToString();
        return strings;
    }

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
        WriteBooleanValue(value);
    }

    public void WriteString(ReadOnlySpan<char> name, ReadOnlySpan<char> value)
    {
        WriteName(name, BlittableBsonConstants.BsonType.String);
        WriteStringValue(value);
    }

    public void WriteObjectId(ReadOnlySpan<char> name, ObjectId value)
    {
        WriteName(name, BlittableBsonConstants.BsonType.ObjectId);
        WriteObjectIdValue(value);
    }

    public void WriteDateTime(ReadOnlySpan<char> name, DateTime value)
    {
        WriteName(name, BlittableBsonConstants.BsonType.DateTime);
        WriteDateTimeValue(value);
    }

    public void WriteNull(ReadOnlySpan<char> name)
    {
        WriteName(name, BlittableBsonConstants.BsonType.Null);
    }

    public void WriteBinary(ReadOnlySpan<char> name, ReadOnlySpan<byte> bytes, byte subtype = 0)
    {
        WriteName(name, BlittableBsonConstants.BsonType.Binary);
        WriteBinaryValue(bytes, subtype);
    }

    public void WriteGuid(ReadOnlySpan<char> name, Guid value)
    {
        WriteName(name, BlittableBsonConstants.BsonType.Binary);
        WriteGuidValue(value);
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

    // Value-only versions for converters
    public void WriteInt32Value(int value)
    {
        Span<byte> bytes = stackalloc byte[4];
        Unsafe.WriteUnaligned(ref MemoryMarshal.GetReference(bytes), value);
        _buffer.AddRange(bytes);
    }

    public void WriteInt64Value(long value)
    {
        Span<byte> bytes = stackalloc byte[8];
        Unsafe.WriteUnaligned(ref MemoryMarshal.GetReference(bytes), value);
        _buffer.AddRange(bytes);
    }

    public void WriteDoubleValue(int value) => WriteDoubleValue((double)value);
    public void WriteDoubleValue(double value)
    {
        Span<byte> bytes = stackalloc byte[8];
        Unsafe.WriteUnaligned(ref MemoryMarshal.GetReference(bytes), value);
        _buffer.AddRange(bytes);
    }

    public void WriteBooleanValue(bool value) => _buffer.Add(value ? (byte)1 : (byte)0);

    public void WriteStringValue(ReadOnlySpan<char> value)
    {
        int byteCount = Encoding.UTF8.GetByteCount(value);
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

    public void WriteObjectIdValue(ObjectId value)
    {
        // ObjectId stores fields in a way that needs careful byte ordering.
        // The driver's ToByteArray is the safest baseline for correctness.
        var bytes = value.ToByteArray();
        _buffer.AddRange(bytes);
    }

    public void WriteDateTimeValue(DateTime value)
    {
        long ms = (long)(value.ToUniversalTime() - DateTime.UnixEpoch).TotalMilliseconds;
        WriteInt64Value(ms);
    }

    public void WriteBinaryValue(ReadOnlySpan<byte> bytes, byte subtype = 0)
    {
        WriteInt32Value(bytes.Length);
        _buffer.Add(subtype);
        _buffer.AddRange(bytes);
    }

    public void WriteGuidValue(Guid value)
    {
        Span<byte> bytes = stackalloc byte[16];
        value.TryWriteBytes(bytes, bigEndian: true, out _);
        WriteBinaryValue(bytes, 4);
    }

    public void WriteRaw(ReadOnlySpan<byte> bytes)
    {
        _buffer.AddRange(bytes);
    }

    // Value-only versions for arrays (using index as string key)
    public void WriteInt32(int index, int value)
    {
        if ((uint)index < (uint)IndexStrings.Length)
        {
            WriteInt32(IndexStrings[index], value);
        }
        else
        {
            Span<char> name = stackalloc char[11];
            index.TryFormat(name, out int charsWritten);
            WriteInt32(name.Slice(0, charsWritten), value);
        }
    }

    public void WriteString(int index, ReadOnlySpan<char> value)
    {
        if ((uint)index < (uint)IndexStrings.Length)
        {
            WriteString(IndexStrings[index], value);
        }
        else
        {
            Span<char> name = stackalloc char[11];
            index.TryFormat(name, out int charsWritten);
            WriteString(name.Slice(0, charsWritten), value);
        }
    }
}
