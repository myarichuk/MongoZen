using MongoDB.Bson;
using MongoZen.Bson;
using SharpArena.Allocators;
using SharpArena.Collections;

namespace MongoZen.ChangeTracking;

/// <summary>
/// A zero-allocation update builder that writes directly to an ArenaBsonWriter.
/// It maintains state for various MongoDB operators ($set, $unset, etc.) and
/// renders them into a single update document.
/// </summary>
public unsafe struct ArenaUpdateDefinitionBuilder
{
    private ArenaBsonWriter _writer;
    private readonly ArenaAllocator _arena;
    private readonly char* _pathBuffer;
    private readonly int _pathBufferLength;
    private bool _hasSet = false;
    private bool _hasUnset = false;

    public ArenaUpdateDefinitionBuilder(ArenaAllocator arena, char* pathBuffer, int pathBufferLength = 256)
    {
        _writer = new ArenaBsonWriter(arena);
        _arena = arena;
        _pathBuffer = pathBuffer;
        _pathBufferLength = pathBufferLength;
    }

    public ArenaUpdateDefinitionBuilder(ArenaAllocator arena)
        : this(arena, (char*)arena.Alloc(256 * sizeof(char)), 256) { }

    public readonly bool HasChanges => _hasSet || _hasUnset;

    public ReadOnlySpan<char> CombinePath(ReadOnlySpan<char> prefix, string elementName)
    {
        if (prefix.Length == 0)
        {
            return elementName.AsSpan();
        }

        int totalLength = prefix.Length + 1 + elementName.Length;
        if (totalLength > _pathBufferLength)
        {
            // Overflow: encode the combined path as UTF-8 into the arena.
            // Callers must use CombinePathUtf8 and the ArenaUtf8String WriteName overload.
            // This overload falls through to that path by returning empty — callers should
            // prefer CombinePathUtf8 directly when the prefix is known to be long.
            return CombinePathToBuffer(prefix, elementName, totalLength);
        }

        prefix.CopyTo(new Span<char>(_pathBuffer, _pathBufferLength));
        _pathBuffer[prefix.Length] = '.';
        elementName.AsSpan().CopyTo(new Span<char>(_pathBuffer + prefix.Length + 1, _pathBufferLength - prefix.Length - 1));
        return new ReadOnlySpan<char>(_pathBuffer, totalLength);
    }

    private ReadOnlySpan<char> CombinePathToBuffer(ReadOnlySpan<char> prefix, string elementName, int totalLength)
    {
        // Allocate a char buffer in the arena for paths that overflow the stack buffer.
        // Arena lifetime = session lifetime, so the span is safe to use until Dispose.
        var extended = (char*)_arena.Alloc((nuint)(totalLength * sizeof(char)));
        prefix.CopyTo(new Span<char>(extended, totalLength));
        extended[prefix.Length] = '.';
        elementName.AsSpan().CopyTo(new Span<char>(extended + prefix.Length + 1, elementName.Length));
        return new ReadOnlySpan<char>(extended, totalLength);
    }

    /// <summary>
    /// Combines a path prefix and element name into an arena-lifetime UTF-8 view.
    /// Use this instead of <see cref="CombinePath"/> when the combined path may exceed 256 chars,
    /// and pair it with the <c>WriteName(ArenaUtf8String, ...)</c> overload to avoid re-encoding.
    /// </summary>
    public ArenaUtf8String CombinePathUtf8(ReadOnlySpan<char> prefix, string elementName)
    {
        if (prefix.Length == 0)
        {
            return ArenaUtf8String.Clone(elementName.AsSpan(), _arena);
        }

        int totalLength = prefix.Length + 1 + elementName.Length;
        Span<char> tmp = totalLength <= 512
            ? stackalloc char[totalLength]
            : new char[totalLength]; // very deep nesting edge case

        prefix.CopyTo(tmp);
        tmp[prefix.Length] = '.';
        elementName.AsSpan().CopyTo(tmp[(prefix.Length + 1)..]);
        return ArenaUtf8String.Clone(tmp[..totalLength], _arena);
    }

    private void EnsureSetStarted()
    {
        if (_hasSet)
        {
            return;
        }

        if (!_hasUnset)
        {
            _writer.WriteStartDocument();
        }
        else
        {
            _writer.WriteEndDocument(); // close $unset
        }
        
        _writer.WriteStartDocument("$set");
        _hasSet = true;
    }

    private void EnsureUnsetStarted()
    {
        if (_hasUnset)
        {
            return;
        }

        if (_hasSet)
        {
            _writer.WriteEndDocument(); // close $set
        }
        else
        {
            _writer.WriteStartDocument();
        }

        _writer.WriteStartDocument("$unset");
        _hasUnset = true;
    }

    public void Set(ReadOnlySpan<char> path, int value)
    {
        EnsureSetStarted();
        _writer.WriteInt32(path, value);
    }

    public void Set(ReadOnlySpan<char> path, long value)
    {
        EnsureSetStarted();
        _writer.WriteInt64(path, value);
    }

    public void Set(ReadOnlySpan<char> path, double value)
    {
        EnsureSetStarted();
        _writer.WriteDouble(path, value);
    }

    public void Set(ReadOnlySpan<char> path, bool value)
    {
        EnsureSetStarted();
        _writer.WriteBoolean(path, value);
    }

    public void Set(ReadOnlySpan<char> path, string? value)
    {
        EnsureSetStarted();
        if (value == null)
        {
            _writer.WriteNull(path);
        }
        else
        {
            _writer.WriteString(path, value.AsSpan());
        }
    }

    public void Set(ReadOnlySpan<char> path, ObjectId value)
    {
        EnsureSetStarted();
        _writer.WriteObjectId(path, value);
    }

    public void Set(ReadOnlySpan<char> path, DateTime value)
    {
        EnsureSetStarted();
        _writer.WriteDateTime(path, value);
    }

    public void Set(ReadOnlySpan<char> path, Guid value)
    {
        EnsureSetStarted();
        _writer.WriteGuid(path, value);
    }

    public void Set(ReadOnlySpan<char> path, decimal value)
    {
        EnsureSetStarted();
        _writer.WriteDecimal128(path, value);
    }

    public void SetNull(ReadOnlySpan<char> path)
    {
        EnsureSetStarted();
        _writer.WriteNull(path);
    }

    public void Set(ReadOnlySpan<char> path, BsonValue value)
    {
        EnsureSetStarted();
        if (value.IsInt32)
        {
            _writer.WriteInt32(path, value.AsInt32);
        }
        else if (value.IsInt64)
        {
            _writer.WriteInt64(path, value.AsInt64);
        }
        else if (value.IsDouble)
        {
            _writer.WriteDouble(path, value.AsDouble);
        }
        else if (value.IsBoolean)
        {
            _writer.WriteBoolean(path, value.AsBoolean);
        }
        else if (value.IsString)
        {
            _writer.WriteString(path, value.AsString.AsSpan());
        }
        else if (value.IsObjectId)
        {
            _writer.WriteObjectId(path, value.AsObjectId);
        }
        else if (value.BsonType == BsonType.DateTime)
        {
            _writer.WriteDateTime(path, value.ToUniversalTime());
        }
        else if (value.IsGuid)
        {
            _writer.WriteGuid(path, value.AsGuid);
        }
        else if (value.IsBsonNull)
        {
            _writer.WriteNull(path);
        }
        else
        {
            _writer.WriteName(path, (BlittableBsonConstants.BsonType)value.BsonType);
            using var ms = new System.IO.MemoryStream();
            using (var bsonWriter = new MongoDB.Bson.IO.BsonBinaryWriter(ms))
            {
                bsonWriter.WriteStartDocument();
                bsonWriter.WriteName("v");
                MongoDB.Bson.Serialization.BsonSerializer.Serialize(bsonWriter, value);
                bsonWriter.WriteEndDocument();
            }
            var bytes = ms.ToArray();
            _writer.WriteRaw(new ReadOnlySpan<byte>(bytes, 7, bytes.Length - 8));
        }
    }

    /// <summary>
    /// <c>$set</c> of any supported value, written by the same <see cref="ElementCodec{T}"/> the insert
    /// path uses, so an update and an insert of the same value produce byte-identical BSON. A null
    /// value is written as BSON null.
    /// </summary>
    public void SetValue<T>(ReadOnlySpan<char> path, T value)
    {
        EnsureSetStarted();
        ElementCodec<T>.Write(ref _writer, path, value);
    }

    /// <summary>Kept for source compatibility; identical to <see cref="SetValue{T}"/>.</summary>
    public void SetObject<T>(ReadOnlySpan<char> path, T value) => SetValue(path, value);

    // ArenaUtf8String overloads — use with CombinePathUtf8 to avoid double UTF-8 encoding on deep paths
    public void Set(ArenaUtf8String path, int value) { EnsureSetStarted(); _writer.WriteName(path, BlittableBsonConstants.BsonType.Int32); _writer.WriteInt32Value(value); }
    public void Set(ArenaUtf8String path, long value) { EnsureSetStarted(); _writer.WriteName(path, BlittableBsonConstants.BsonType.Int64); _writer.WriteInt64Value(value); }
    public void Set(ArenaUtf8String path, double value) { EnsureSetStarted(); _writer.WriteName(path, BlittableBsonConstants.BsonType.Double); _writer.WriteDoubleValue(value); }
    public void Set(ArenaUtf8String path, bool value) { EnsureSetStarted(); _writer.WriteName(path, BlittableBsonConstants.BsonType.Boolean); _writer.WriteBooleanValue(value); }
    public void Set(ArenaUtf8String path, string? value) { EnsureSetStarted(); if (value == null) _writer.WriteName(path, BlittableBsonConstants.BsonType.Null); else { _writer.WriteName(path, BlittableBsonConstants.BsonType.String); _writer.WriteStringValue(value.AsSpan()); } }
    public void Set(ArenaUtf8String path, Guid value) { EnsureSetStarted(); _writer.WriteName(path, BlittableBsonConstants.BsonType.Binary); _writer.WriteGuidValue(value); }
    public void Set(ArenaUtf8String path, DateTime value) { EnsureSetStarted(); _writer.WriteName(path, BlittableBsonConstants.BsonType.DateTime); _writer.WriteDateTimeValue(value); }
    public void Set(ArenaUtf8String path, decimal value) { EnsureSetStarted(); _writer.WriteName(path, BlittableBsonConstants.BsonType.Decimal128); _writer.WriteDecimal128Value(value); }
    public void Unset(ArenaUtf8String path) { EnsureUnsetStarted(); _writer.WriteName(path, BlittableBsonConstants.BsonType.Int32); _writer.WriteInt32Value(1); }

    public void SetRaw(ReadOnlySpan<char> path, ReadOnlySpan<byte> bsonValue, BlittableBsonConstants.BsonType type)
    {
        EnsureSetStarted();
        _writer.WriteName(path, type);
        _writer.WriteRaw(bsonValue);
    }

    public void Unset(ReadOnlySpan<char> path)
    {
        EnsureUnsetStarted();
        _writer.WriteInt32(path, 1); // $unset: { field: 1 }
    }

    public BlittableBsonDocument Build()
    {
        if (!HasChanges)
        {
            return default;
        }

        _writer.WriteEndDocument(); // Close the last open operator document ($set or $unset)
        _writer.WriteEndDocument(); // Close root document
        
        return _writer.Commit(_arena);
    }
}
