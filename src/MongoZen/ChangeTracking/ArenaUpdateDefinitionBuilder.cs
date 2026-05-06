using MongoDB.Bson;
using MongoZen.Bson;
using SharpArena.Allocators;

namespace MongoZen.ChangeTracking;

/// <summary>
/// A zero-allocation update builder that writes directly to an ArenaBsonWriter.
/// It maintains state for various MongoDB operators ($set, $unset, etc.) and
/// renders them into a single update document.
/// </summary>
public struct ArenaUpdateDefinitionBuilder
{
    private ArenaBsonWriter _writer;
    private readonly ArenaAllocator _arena;
    private readonly char[] _pathBuffer;
    private bool _hasSet;
    private bool _hasUnset;

    public ArenaUpdateDefinitionBuilder(ArenaAllocator arena) : this(arena, new char[256]) { }

    public ArenaUpdateDefinitionBuilder(ArenaAllocator arena, char[] pathBuffer)
    {
        _writer = new ArenaBsonWriter(arena);
        _arena = arena;
        _pathBuffer = pathBuffer ?? new char[256];
        _hasSet = false;
        _hasUnset = false;
    }

    public readonly bool HasChanges => _hasSet || _hasUnset;

    public ReadOnlySpan<char> CombinePath(ReadOnlySpan<char> prefix, string elementName)
    {
        if (prefix.Length == 0)
        {
            return elementName.AsSpan();
        }

        int totalLength = prefix.Length + 1 + elementName.Length;
        if (totalLength > _pathBuffer.Length)
        {
            // Fallback for extremely deep nesting
            return string.Concat(prefix.ToString(), ".", elementName).AsSpan();
        }

        prefix.CopyTo(_pathBuffer);
        _pathBuffer[prefix.Length] = '.';
        elementName.AsSpan().CopyTo(_pathBuffer.AsSpan(prefix.Length + 1));
        return new ReadOnlySpan<char>(_pathBuffer, 0, totalLength);
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

    public void SetObject<T>(ReadOnlySpan<char> path, T value)
    {
        EnsureSetStarted();
        if (value == null)
        {
            _writer.WriteNull(path);
            return;
        }

        if (TypeCache<T>.IsString) { _writer.WriteString(path, (value as string).AsSpan()); return; }
        if (TypeCache<T>.IsInt) { _writer.WriteInt32(path, (int)(object)value); return; }
        if (TypeCache<T>.IsLong) { _writer.WriteInt64(path, (long)(object)value); return; }
        if (TypeCache<T>.IsDouble) { _writer.WriteDouble(path, (double)(object)value); return; }
        if (TypeCache<T>.IsBool) { _writer.WriteBoolean(path, (bool)(object)value); return; }
        if (TypeCache<T>.IsObjectId) { _writer.WriteObjectId(path, (ObjectId)(object)value); return; }
        if (TypeCache<T>.IsGuid) { _writer.WriteGuid(path, (Guid)(object)value); return; }
        if (TypeCache<T>.IsDateTime) { _writer.WriteDateTime(path, (DateTime)(object)value); return; }
        if (TypeCache<T>.IsDecimal) { _writer.WriteDecimal128(path, (decimal)(object)value); return; }

        if (TypeCache<T>.IsCollection || TypeCache<T>.IsDictionary)
        {
            _writer.WriteName(path, TypeCache<T>.BsonType);
            BlittableConverter<T>.Instance.Write(ref _writer, value);
            return;
        }

        _writer.WriteName(path, BlittableBsonConstants.BsonType.Document);
        BlittableConverter<T>.Instance.Write(ref _writer, value);
    }

    private static class TypeCache<T>
    {
        public static readonly bool IsString = typeof(T) == typeof(string);
        public static readonly bool IsInt = typeof(T) == typeof(int);
        public static readonly bool IsLong = typeof(T) == typeof(long);
        public static readonly bool IsDouble = typeof(T) == typeof(double);
        public static readonly bool IsBool = typeof(T) == typeof(bool);
        public static readonly bool IsObjectId = typeof(T) == typeof(ObjectId);
        public static readonly bool IsGuid = typeof(T) == typeof(Guid);
        public static readonly bool IsDateTime = typeof(T) == typeof(DateTime);
        public static readonly bool IsDecimal = typeof(T) == typeof(decimal);
        public static readonly bool IsCollection;
        public static readonly bool IsDictionary;
        public static readonly BlittableBsonConstants.BsonType BsonType;

        static TypeCache()
        {
            IsCollection = ArenaUpdateDefinitionBuilder.IsCollection(typeof(T), out _);
            IsDictionary = ArenaUpdateDefinitionBuilder.IsDictionary(typeof(T), out _);
            BsonType = IsCollection ? BlittableBsonConstants.BsonType.Array : BlittableBsonConstants.BsonType.Document;
        }
    }

    private static bool IsDictionary(Type type, out Type valueType)
    {
        if (type.IsGenericType)
        {
            var def = type.GetGenericTypeDefinition();
            if (def == typeof(Dictionary<,>) || 
                def == typeof(IDictionary<,>) || 
                def == typeof(IReadOnlyDictionary<,>))
            {
                var args = type.GetGenericArguments();
                if (args[0] == typeof(string))
                {
                    valueType = args[1];
                    return true;
                }
            }
        }

        valueType = null!;
        return false;
    }

    private static bool IsCollection(Type type, out Type elementType)
    {
        if (type == typeof(string))
        {
            elementType = null!;
            return false;
        }

        if (type.IsArray)
        {
            elementType = type.GetElementType()!;
            return true;
        }

        if (type.IsGenericType)
        {
            var def = type.GetGenericTypeDefinition();
            if (def == typeof(List<>) || def == typeof(IEnumerable<>) || def == typeof(IReadOnlyList<>) || def == typeof(ICollection<>))
            {
                elementType = type.GetGenericArguments()[0];
                return true;
            }
        }

        elementType = null!;
        return false;
    }

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
