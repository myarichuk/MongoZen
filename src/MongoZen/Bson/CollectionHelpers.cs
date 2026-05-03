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
        writer.WriteStartArray(name);
        int i = 0;
        foreach (var item in collection)
        {
            EmitValue(ref writer, i++, item);
        }
        writer.WriteEndArray();
    }

    private static void EmitValue(ref ArenaBsonWriter writer, int index, T value)
    {
        Span<char> name = stackalloc char[11];
        index.TryFormat(name, out int charsWritten);
        var nameSpan = name.Slice(0, charsWritten);

        if (typeof(T) == typeof(int)) writer.WriteInt32(nameSpan, (int)(object)value!);
        else if (typeof(T) == typeof(string)) writer.WriteString(nameSpan, (string)(object)value!);
        else if (typeof(T) == typeof(long)) writer.WriteInt64(nameSpan, (long)(object)value!);
        else if (typeof(T) == typeof(double)) writer.WriteDouble(nameSpan, (double)(object)value!);
        else if (typeof(T) == typeof(bool)) writer.WriteBoolean(nameSpan, (bool)(object)value!);
        else if (typeof(T) == typeof(ObjectId)) writer.WriteObjectId(nameSpan, (ObjectId)(object)value!);
        else if (typeof(T) == typeof(DateTime)) writer.WriteDateTime(nameSpan, (DateTime)(object)value!);
        else
        {
            writer.WriteName(nameSpan, BlittableBsonConstants.BsonType.Document);
            BlittableConverter<T>.Instance.Write(ref writer, value);
        }
    }

    public static T[] ReadArray(BlittableBsonArray array, ArenaAllocator arena)
    {
        var result = new T[array.Count];
        var type = typeof(T);
        bool isComplexPoco = (type.IsClass || (type.IsValueType && !type.IsPrimitive && !type.IsEnum)) && 
                             type != typeof(string) && type != typeof(decimal) && 
                             type != typeof(ObjectId) && type != typeof(Guid) &&
                             !(type.Namespace?.StartsWith("MongoDB.Bson") ?? false);

        if (isComplexPoco)
        {
            var deserializer = DynamicBlittableSerializer<T>.DeserializeDelegate;
            for (int i = 0; i < array.Count; i++)
            {
                result[i] = deserializer(array[i].GetDocument(), arena);
            }
        }
        else
        {
            for (int i = 0; i < array.Count; i++)
            {
                result[i] = array[i].Get<T>();
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
            EmitValue(ref writer, kvp.Key, kvp.Value);
        }
        writer.WriteEndDocument();
    }

    private static void EmitValue(ref ArenaBsonWriter writer, string key, TValue value)
    {
        var type = typeof(TValue);
        if (type == typeof(int)) writer.WriteInt32(key, (int)(object)value!);
        else if (type == typeof(string)) writer.WriteString(key, (string)(object)value!);
        else if (type == typeof(long)) writer.WriteInt64(key, (long)(object)value!);
        else if (type == typeof(double)) writer.WriteDouble(key, (double)(object)value!);
        else if (type == typeof(bool)) writer.WriteBoolean(key, (bool)(object)value!);
        else if (type == typeof(ObjectId)) writer.WriteObjectId(key, (ObjectId)(object)value!);
        else if (type == typeof(DateTime)) writer.WriteDateTime(key, (DateTime)(object)value!);
        else
        {
            writer.WriteName(key, BlittableBsonConstants.BsonType.Document);
            BlittableConverter<TValue>.Instance.Write(ref writer, value);
        }
    }

    public static Dictionary<string, TValue> ReadDictionary(BlittableBsonDocument doc, ArenaAllocator arena)
    {
        var result = new Dictionary<string, TValue>();
        var type = typeof(TValue);
        bool isComplexPoco = (type.IsClass || (type.IsValueType && !type.IsPrimitive && !type.IsEnum)) && 
                             type != typeof(string) && type != typeof(decimal) && 
                             type != typeof(ObjectId) && type != typeof(Guid) &&
                             !(type.Namespace?.StartsWith("MongoDB.Bson") ?? false);

        foreach (var key in doc.Keys)
        {
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
