using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using SharpArena.Allocators;
using SharpArena.Collections;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;

namespace MongoZen.Bson;

// Cached per enum type: asks the driver's own serializer (attribute- and convention-driven) whether
// an enum type is configured for string representation, so the fast route (both the property-level
// code in DynamicBlittableSerializer and the collection/dictionary element code here) stays
// consistent with whatever the driver would actually produce for that type.
internal static class EnumRepresentation
{
    private static readonly ConcurrentDictionary<Type, bool> Cache = new();

    public static bool IsString(Type enumType) =>
        Cache.GetOrAdd(enumType, static t =>
            BsonSerializer.LookupSerializer(t) is IHasRepresentationSerializer { Representation: BsonType.String });
}

public static class CollectionHelper<T>
{
    public static bool EqualsSnapshotAtOffset(IEnumerable<T>? collection, BlittableBsonDocument doc, int offset, ArenaAllocator arena)
    {
        var type = ArenaBsonReader.GetElementType(doc, offset);
        if (type == BlittableBsonConstants.BsonType.Null)
        {
            return collection == null;
        }

        if (collection == null)
        {
            return false;
        }

        if (type != BlittableBsonConstants.BsonType.Array)
        {
            return false;
        }

        return EqualsSnapshot(collection, doc.GetArray(offset, arena), arena);
    }

    public static bool EqualsSnapshot(IEnumerable<T>? collection, BlittableBsonArray snapshot, ArenaAllocator arena)
    {
        if (collection == null)
        {
            return snapshot.Count == 0;
        }

        if (GetCount(collection) != snapshot.Count)
        {
            return false;
        }

        int i = 0;
        foreach (var item in collection)
        {
            if (!ElementEquals(item, snapshot[i], arena))
            {
                return false;
            }

            i++;
        }

        return true;
    }

    private static int GetCount(IEnumerable<T> collection) =>
        collection switch
        {
            T[] arr => arr.Length,
            ICollection<T> col => col.Count,
            IReadOnlyCollection<T> roc => roc.Count,
            _ => collection.Count()
        };

    private static bool ElementEquals(T? item, BlittableBsonArray.Element element, ArenaAllocator arena)
    {
        if (item == null)
        {
            return element.Type == BlittableBsonConstants.BsonType.Null;
        }

        if (element.Type == BlittableBsonConstants.BsonType.Null)
        {
            return false;
        }

        var type = typeof(T);
        if (type.IsEnum)
        {
            if (EnumRepresentation.IsString(type))
            {
                return item.ToString() == element.GetString();
            }

            var underlying = Enum.GetUnderlyingType(type);
            if (underlying == typeof(int))
            {
                return Convert.ToInt32(item) == element.GetInt32();
            }

            if (underlying == typeof(long))
            {
                return Convert.ToInt64(item) == element.Get<long>();
            }

            return false;
        }

        if (type == typeof(int))
        {
            return (int)(object)item == element.GetInt32();
        }

        if (type == typeof(long))
        {
            return (long)(object)item == element.Get<long>();
        }

        if (type == typeof(double))
        {
            return (double)(object)item == element.Get<double>();
        }

        if (type == typeof(bool))
        {
            return (bool)(object)item == element.Get<bool>();
        }

        if (type == typeof(string))
        {
            return (string)(object)item == element.GetString();
        }

        if (type == typeof(ObjectId))
        {
            return (ObjectId)(object)item == element.Get<ObjectId>();
        }

        if (type == typeof(DateTime))
        {
            return (DateTime)(object)item == element.Get<DateTime>();
        }

        if (type == typeof(Guid))
        {
            return (Guid)(object)item == element.Get<Guid>();
        }

        if (type == typeof(decimal))
        {
            return (decimal)(object)item == element.Get<decimal>();
        }

        if (IsComplexPocoType(type))
        {
            if (element.Type != BlittableBsonConstants.BsonType.Document)
            {
                return false;
            }

            var writer = new ArenaBsonWriter(arena);
            DynamicBlittableSerializer<T>.SerializeDelegate(ref writer, item);
            var itemDoc = writer.Commit(arena);
            return BsonElementComparison.DocumentsEqual(itemDoc, element.GetDocument());
        }

        return object.Equals(item, element.Get<T>());
    }

    internal static bool IsComplexPocoType(Type type) =>
        (type.IsClass || (type.IsValueType && !type.IsPrimitive && !type.IsEnum)) &&
        type != typeof(string) && type != typeof(decimal) &&
        type != typeof(ObjectId) && type != typeof(Guid) &&
        !(type.Namespace?.StartsWith("MongoDB.Bson") ?? false);
    public static void WriteArray(ref ArenaBsonWriter writer, ReadOnlySpan<char> name, IEnumerable<T> collection)
    {
        if (collection is List<T> list)
        {
            WriteList(ref writer, name, list);
            return;
        }
        
        if (collection is T[] array)
        {
            WriteSpan(ref writer, name, array);
            return;
        }

        writer.WriteStartArray(name);
        int i = 0;
        foreach (var item in collection)
        {
            EmitValue(ref writer, i++, item);
        }
        writer.WriteEndArray();
    }

    public static void WriteList(ref ArenaBsonWriter writer, ReadOnlySpan<char> name, List<T> list)
    {
        writer.WriteStartArray(name);
        for (int i = 0; i < list.Count; i++)
        {
            EmitValue(ref writer, i, list[i]);
        }
        writer.WriteEndArray();
    }

    public static void WriteSpan(ref ArenaBsonWriter writer, ReadOnlySpan<char> name, ReadOnlySpan<T> span)
    {
        writer.WriteStartArray(name);
        for (int i = 0; i < span.Length; i++)
        {
            EmitValue(ref writer, i, span[i]);
        }
        writer.WriteEndArray();
    }

    private static void EmitValue(ref ArenaBsonWriter writer, int index, T value)
    {
        Span<char> name = stackalloc char[11];
        index.TryFormat(name, out int charsWritten);
        var nameSpan = name[..charsWritten];

        if (value == null)
        {
            writer.WriteNull(nameSpan);
            return;
        }

        if (typeof(T) == typeof(int))
        {
            writer.WriteInt32(nameSpan, (int)(object)value!);
        }
        else if (typeof(T) == typeof(string))
        {
            writer.WriteString(nameSpan, (string)(object)value!);
        }
        else if (typeof(T) == typeof(long))
        {
            writer.WriteInt64(nameSpan, (long)(object)value!);
        }
        else if (typeof(T) == typeof(double))
        {
            writer.WriteDouble(nameSpan, (double)(object)value!);
        }
        else if (typeof(T) == typeof(bool))
        {
            writer.WriteBoolean(nameSpan, (bool)(object)value!);
        }
        else if (typeof(T) == typeof(ObjectId))
        {
            writer.WriteObjectId(nameSpan, (ObjectId)(object)value!);
        }
        else if (typeof(T) == typeof(DateTime))
        {
            writer.WriteDateTime(nameSpan, (DateTime)(object)value!);
        }
        else
        {
            writer.WriteName(nameSpan, BlittableBsonConstants.BsonType.Document);
            BlittableConverter<T>.Instance.Write(ref writer, value);
        }
    }

    public static BsonValue ToBsonValue(IEnumerable<T> collection)
    {
        if (collection == null)
        {
            return BsonNull.Value;
        }

        var type = typeof(T);
        if (type.IsEnum && EnumRepresentation.IsString(type))
        {
            return new BsonArray(collection.Select(x => x == null ? BsonNull.Value : (BsonValue)x.ToString()!));
        }

        if (type.IsPrimitive || type == typeof(string) || type == typeof(decimal) || type == typeof(ObjectId) || type == typeof(Guid) || type == typeof(DateTime) || type.IsEnum)
        {
            return BsonValue.Create(collection);
        }

        return new BsonArray(collection.Select(x => x == null ? BsonNull.Value : (BsonValue)BsonDocumentWrapper.Create(x)));
    }

    public static T[] ReadArray(BlittableBsonArray array, ArenaAllocator arena)
    {
        var result = new T[array.Count];
        var type = typeof(T);
        bool isComplexPoco = IsComplexPocoType(type);

        for (int i = 0; i < array.Count; i++)
        {
            var element = array[i];
            if (element.Type == BlittableBsonConstants.BsonType.Null)
            {
                result[i] = default!;
                continue;
            }

            if (isComplexPoco)
            {
                result[i] = DynamicBlittableSerializer<T>.DeserializeDelegate(element.GetDocument(), arena);
            }
            else
            {
                result[i] = element.Get<T>();
            }
        }
        return result;
    }

    public static List<T> ReadList(BlittableBsonArray array, ArenaAllocator arena)
    {
        var result = new List<T>(array.Count);
        var type = typeof(T);
        bool isComplexPoco = IsComplexPocoType(type);

        for (int i = 0; i < array.Count; i++)
        {
            var element = array[i];
            if (element.Type == BlittableBsonConstants.BsonType.Null)
            {
                result.Add(default!);
                continue;
            }

            if (isComplexPoco)
            {
                result.Add(DynamicBlittableSerializer<T>.DeserializeDelegate(element.GetDocument(), arena));
            }
            else
            {
                result.Add(element.Get<T>());
            }
        }
        return result;
    }
}

public static class DictionaryHelper<TValue>
{
    public static bool EqualsSnapshotAtOffset(IDictionary<string, TValue>? dictionary, BlittableBsonDocument doc, int offset, ArenaAllocator arena)
    {
        var type = ArenaBsonReader.GetElementType(doc, offset);
        if (type == BlittableBsonConstants.BsonType.Null)
        {
            return dictionary == null;
        }

        if (dictionary == null)
        {
            return false;
        }

        if (type != BlittableBsonConstants.BsonType.Document)
        {
            return false;
        }

        return EqualsSnapshot(dictionary, doc.GetDocument(offset, arena), arena);
    }

    public static bool EqualsSnapshot(IDictionary<string, TValue>? dictionary, BlittableBsonDocument snapshot, ArenaAllocator arena)
    {
        if (dictionary == null)
        {
            return !snapshot.KeysEnumerable.Any();
        }

        if (dictionary.Count != snapshot.KeysEnumerable.Count())
        {
            return false;
        }

        foreach (var kvp in dictionary)
        {
            if (!snapshot.TryGetElementOffset(kvp.Key, out var offset))
            {
                return false;
            }

            if (!ValueEquals(kvp.Value, snapshot, offset, arena))
            {
                return false;
            }
        }

        return true;
    }

    private static bool ValueEquals(TValue? value, BlittableBsonDocument snapshot, int offset, ArenaAllocator arena)
    {
        var snapType = ArenaBsonReader.GetElementType(snapshot, offset);
        if (value == null)
        {
            return snapType == BlittableBsonConstants.BsonType.Null;
        }

        if (snapType == BlittableBsonConstants.BsonType.Null)
        {
            return false;
        }

        var type = typeof(TValue);
        if (type.IsEnum)
        {
            if (EnumRepresentation.IsString(type))
            {
                return value.ToString() == snapshot.GetString(offset);
            }

            var underlying = Enum.GetUnderlyingType(type);
            if (underlying == typeof(int))
            {
                return Convert.ToInt32(value) == snapshot.GetInt32(offset);
            }

            if (underlying == typeof(long))
            {
                return Convert.ToInt64(value) == snapshot.GetInt64(offset);
            }

            return false;
        }

        if (type == typeof(int))
        {
            return (int)(object)value == snapshot.GetInt32(offset);
        }

        if (type == typeof(long))
        {
            return (long)(object)value == snapshot.GetInt64(offset);
        }

        if (type == typeof(double))
        {
            return (double)(object)value == snapshot.GetDouble(offset);
        }

        if (type == typeof(bool))
        {
            return (bool)(object)value == snapshot.GetBoolean(offset);
        }

        if (type == typeof(string))
        {
            return (string)(object)value == snapshot.GetString(offset);
        }

        if (type == typeof(ObjectId))
        {
            return (ObjectId)(object)value == snapshot.GetObjectId(offset);
        }

        if (type == typeof(DateTime))
        {
            return (DateTime)(object)value == snapshot.GetDateTime(offset);
        }

        if (type == typeof(Guid))
        {
            return (Guid)(object)value == snapshot.GetGuid(offset);
        }

        if (type == typeof(decimal))
        {
            return (decimal)(object)value == snapshot.GetDecimal128(offset);
        }

        if (CollectionHelper<TValue>.IsComplexPocoType(type))
        {
            if (snapType != BlittableBsonConstants.BsonType.Document)
            {
                return false;
            }

            var writer = new ArenaBsonWriter(arena);
            DynamicBlittableSerializer<TValue>.SerializeDelegate(ref writer, value);
            var valueDoc = writer.Commit(arena);
            return BsonElementComparison.DocumentsEqual(valueDoc, snapshot.GetDocument(offset, arena));
        }

        return object.Equals(value, snapshot.Get<TValue>(offset));
    }

    public static void WriteDictionary(ref ArenaBsonWriter writer, ReadOnlySpan<char> name, IDictionary<string, TValue> dictionary)
    {
        writer.WriteStartDocument(name);
        foreach (var kvp in dictionary)
        {
            if (typeof(TValue) == typeof(int))
            {
                writer.WriteInt32(kvp.Key, (int)(object)kvp.Value!);
            }
            else if (typeof(TValue) == typeof(string))
            {
                writer.WriteString(kvp.Key, (string)(object)kvp.Value!);
            }
            else
            {
                writer.WriteName(kvp.Key, BlittableBsonConstants.BsonType.Document);
                BlittableConverter<TValue>.Instance.Write(ref writer, kvp.Value);
            }
        }
        writer.WriteEndDocument();
    }

    public static BsonValue ToBsonValue(IDictionary<string, TValue> dictionary)
    {
        if (dictionary == null)
        {
            return BsonNull.Value;
        }

        var type = typeof(TValue);
        if (type.IsEnum)
        {
            var isString = EnumRepresentation.IsString(type);
            var enumDoc = new BsonDocument();
            foreach (var kvp in dictionary)
            {
                enumDoc[kvp.Key] = kvp.Value == null
                    ? BsonNull.Value
                    : isString ? kvp.Value.ToString()! : BsonValue.Create(kvp.Value);
            }

            return enumDoc;
        }

        if (type.IsPrimitive || type == typeof(string) || type == typeof(decimal) || type == typeof(ObjectId) || type == typeof(Guid) || type == typeof(DateTime))
        {
            return BsonValue.Create(dictionary);
        }

        var doc = new BsonDocument();
        foreach (var kvp in dictionary)
        {
            doc[kvp.Key] = kvp.Value == null ? BsonNull.Value : BsonDocumentWrapper.Create(kvp.Value);
        }
        return doc;
    }

    public static Dictionary<string, TValue> ReadDictionary(BlittableBsonDocument doc, ArenaAllocator arena)
    {
        var result = new Dictionary<string, TValue>();
        foreach (var key in doc.KeysEnumerable)
        {
            var type = typeof(TValue);
            bool isComplexPoco = CollectionHelper<TValue>.IsComplexPocoType(type);

            if (isComplexPoco)
            {
                result[key.ToString()] = DynamicBlittableSerializer<TValue>.DeserializeDelegate(doc.GetDocument(key, arena), arena);
            }
            else
            {
                result[key.ToString()] = doc.Get<TValue>(key);
            }
        }
        return result;
    }
}

internal static class BsonElementComparison
{
    public static bool DocumentsEqual(BlittableBsonDocument a, BlittableBsonDocument b)
    {
        if (a.IsDefault || b.IsDefault)
        {
            return a.IsDefault && b.IsDefault;
        }

        if (a.Length != b.Length)
        {
            return false;
        }

        return a.AsReadOnlySpan().SequenceEqual(b.AsReadOnlySpan());
    }
}
