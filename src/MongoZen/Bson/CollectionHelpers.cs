using SharpArena.Allocators;

namespace MongoZen.Bson;

/// <summary>
/// BSON Array support for any <c>IEnumerable&lt;T&gt;</c>. Element handling is delegated to
/// <see cref="ElementCodec{T}"/>, so an element is written/read/compared exactly like a property of the
/// same type would be (a <c>List&lt;Guid&gt;</c> is a list of UUIDs, a <c>List&lt;MyEnum&gt;</c> a list of ints,
/// a <c>List&lt;List&lt;int&gt;&gt;</c> nested arrays, and so on).
/// </summary>
public static unsafe class CollectionHelper<T>
{
    public static void WriteArray(ref ArenaBsonWriter writer, ReadOnlySpan<char> name, IEnumerable<T> collection)
    {
        writer.WriteStartArray(name);
        WriteElements(ref writer, collection);
        writer.WriteEndArray();
    }

    private static void WriteElements(ref ArenaBsonWriter writer, IEnumerable<T> collection)
    {
        Span<char> indexBuffer = stackalloc char[11];

        switch (collection)
        {
            case T[] array:
                for (int i = 0; i < array.Length; i++)
                {
                    ElementCodec<T>.Write(ref writer, FormatIndex(i, indexBuffer), array[i]);
                }
                return;

            case List<T> list:
                for (int i = 0; i < list.Count; i++)
                {
                    ElementCodec<T>.Write(ref writer, FormatIndex(i, indexBuffer), list[i]);
                }
                return;

            default:
            {
                int i = 0;
                foreach (var item in collection)
                {
                    ElementCodec<T>.Write(ref writer, FormatIndex(i++, indexBuffer), item);
                }
                return;
            }
        }
    }

    private static ReadOnlySpan<char> FormatIndex(int index, Span<char> buffer)
    {
        index.TryFormat(buffer, out int written);
        return buffer[..written];
    }

    public static T[] ReadArray(BlittableBsonArray array, ArenaAllocator arena)
    {
        var result = new T[array.Count];
        for (int i = 0; i < result.Length; i++)
        {
            var element = array[i];
            result[i] = ElementCodec<T>.ReadAt(element.Pointer, element.Type, element.Length, arena);
        }

        return result;
    }

    public static List<T> ReadList(BlittableBsonArray array, ArenaAllocator arena)
    {
        var result = new List<T>(array.Count);
        for (int i = 0; i < array.Count; i++)
        {
            var element = array[i];
            result.Add(ElementCodec<T>.ReadAt(element.Pointer, element.Type, element.Length, arena));
        }

        return result;
    }

    /// <summary>Entry point for compiled code (no pointer types in expression trees).</summary>
    public static T[] ReadArrayRaw(nint p, BlittableBsonConstants.BsonType type, int length, ArenaAllocator arena)
    {
        if (type != BlittableBsonConstants.BsonType.Array)
        {
            throw new InvalidCastException($"Cannot cast {type} to Array");
        }

        return ReadArray(new BlittableBsonArray((byte*)p, length, arena), arena);
    }

    public static bool EqualsSnapshotAtOffset(IEnumerable<T>? collection, BlittableBsonDocument doc, int offset, ArenaAllocator arena)
    {
        var p = doc.GetElementData(offset, out var type, out var length);
        return EqualsSnapshotRaw(collection, (nint)p, type, length, arena);
    }

    public static bool EqualsSnapshotRaw(IEnumerable<T>? collection, nint p, BlittableBsonConstants.BsonType type, int length, ArenaAllocator arena)
    {
        if (type == BlittableBsonConstants.BsonType.Null)
        {
            return collection == null;
        }

        if (collection == null || type != BlittableBsonConstants.BsonType.Array)
        {
            return false;
        }

        return EqualsSnapshot(collection, new BlittableBsonArray((byte*)p, length, arena), arena);
    }

    public static bool EqualsSnapshot(IEnumerable<T>? collection, BlittableBsonArray snapshot, ArenaAllocator arena)
    {
        if (collection == null)
        {
            return false;
        }

        if (collection.TryGetNonEnumeratedCount(out var count) && count != snapshot.Count)
        {
            return false;
        }

        int i = 0;
        foreach (var item in collection)
        {
            if (i >= snapshot.Count)
            {
                return false;
            }

            var element = snapshot[i];
            if (!ElementCodec<T>.EqualsAt(item, element.Pointer, element.Type, element.Length, arena))
            {
                return false;
            }

            i++;
        }

        return i == snapshot.Count;
    }
}

/// <summary>
/// BSON Document support for string-keyed dictionaries (anything enumerating
/// <c>KeyValuePair&lt;string, V&gt;</c>). Values go through <see cref="ElementCodec{T}"/>, so a
/// <c>Dictionary&lt;string, double&gt;</c> is a document of doubles, not an exception, and a
/// <c>Dictionary&lt;string, List&lt;int&gt;&gt;</c> a document of arrays.
/// </summary>
public static unsafe class DictionaryHelper<TValue>
{
    public static void WriteDictionary(ref ArenaBsonWriter writer, ReadOnlySpan<char> name, IEnumerable<KeyValuePair<string, TValue>> dictionary)
    {
        writer.WriteStartDocument(name);
        foreach (var kvp in dictionary)
        {
            ElementCodec<TValue>.Write(ref writer, kvp.Key.AsSpan(), kvp.Value);
        }

        writer.WriteEndDocument();
    }

    public static Dictionary<string, TValue> ReadDictionary(BlittableBsonDocument doc, ArenaAllocator arena)
    {
        var result = new Dictionary<string, TValue>(doc.Count);
        foreach (var key in doc.KeysEnumerable)
        {
            if (!doc.TryGetElementOffset(key, out var offset))
            {
                continue;
            }

            var p = doc.GetElementData(offset, out var type, out var length);
            result[key.ToString()] = ElementCodec<TValue>.ReadAt(p, type, length, arena);
        }

        return result;
    }

    /// <summary>Entry point for compiled code (no pointer types in expression trees).</summary>
    public static Dictionary<string, TValue> ReadDictionaryRaw(nint p, BlittableBsonConstants.BsonType type, int length, ArenaAllocator arena)
    {
        if (type != BlittableBsonConstants.BsonType.Document)
        {
            throw new InvalidCastException($"Cannot cast {type} to Document");
        }

        return ReadDictionary(ArenaBsonReader.ReadInPlace((byte*)p, length, arena), arena);
    }

    public static bool EqualsSnapshotAtOffset(IEnumerable<KeyValuePair<string, TValue>>? dictionary, BlittableBsonDocument doc, int offset, ArenaAllocator arena)
    {
        var p = doc.GetElementData(offset, out var type, out var length);
        return EqualsSnapshotRaw(dictionary, (nint)p, type, length, arena);
    }

    public static bool EqualsSnapshotRaw(IEnumerable<KeyValuePair<string, TValue>>? dictionary, nint p, BlittableBsonConstants.BsonType type, int length, ArenaAllocator arena)
    {
        if (type == BlittableBsonConstants.BsonType.Null)
        {
            return dictionary == null;
        }

        if (dictionary == null || type != BlittableBsonConstants.BsonType.Document)
        {
            return false;
        }

        return EqualsSnapshot(dictionary, ArenaBsonReader.ReadInPlace((byte*)p, length, arena), arena);
    }

    public static bool EqualsSnapshot(IEnumerable<KeyValuePair<string, TValue>>? dictionary, BlittableBsonDocument snapshot, ArenaAllocator arena)
    {
        if (dictionary == null)
        {
            return false;
        }

        if (dictionary.TryGetNonEnumeratedCount(out var count) && count != snapshot.Count)
        {
            return false;
        }

        int seen = 0;
        foreach (var kvp in dictionary)
        {
            if (kvp.Key == null || !snapshot.TryGetElementOffset(kvp.Key.AsSpan(), out var offset))
            {
                return false;
            }

            var p = snapshot.GetElementData(offset, out var type, out var length);
            if (!ElementCodec<TValue>.EqualsAt(kvp.Value, p, type, length, arena))
            {
                return false;
            }

            seen++;
        }

        return seen == snapshot.Count;
    }
}
