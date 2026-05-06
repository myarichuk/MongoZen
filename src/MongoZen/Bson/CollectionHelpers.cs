using System;
using System.Collections.Generic;
using System.Linq;
using SharpArena.Allocators;
using SharpArena.Collections;
using MongoDB.Bson;

namespace MongoZen.Bson;

public static class CollectionHelper<T>
{
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
        if (type.IsPrimitive || type == typeof(string) || type == typeof(decimal) || type == typeof(ObjectId) || type == typeof(Guid) || type == typeof(DateTime))
        {
            return BsonValue.Create(collection);
        }

        return new BsonArray(collection.Select(x => x == null ? BsonNull.Value : (BsonValue)BsonDocumentWrapper.Create(x)));
    }

    public static T[] ReadArray(BlittableBsonArray array, ArenaAllocator arena)
    {
        var result = new T[array.Count];
        var type = typeof(T);
        bool isComplexPoco = (type.IsClass || (type.IsValueType && !type.IsPrimitive && !type.IsEnum)) && 
                             type != typeof(string) && type != typeof(decimal) && 
                             type != typeof(ObjectId) && type != typeof(Guid) &&
                             !(type.Namespace?.StartsWith("MongoDB.Bson") ?? false);

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
        bool isComplexPoco = (type.IsClass || (type.IsValueType && !type.IsPrimitive && !type.IsEnum)) && 
                             type != typeof(string) && type != typeof(decimal) && 
                             type != typeof(ObjectId) && type != typeof(Guid) &&
                             !(type.Namespace?.StartsWith("MongoDB.Bson") ?? false);

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
            bool isComplexPoco = (type.IsClass || (type.IsValueType && !type.IsPrimitive && !type.IsEnum)) && 
                                 type != typeof(string) && type != typeof(decimal) && 
                                 type != typeof(ObjectId) && type != typeof(Guid) &&
                                 !(type.Namespace?.StartsWith("MongoDB.Bson") ?? false);

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
