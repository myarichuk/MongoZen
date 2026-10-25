using System.Collections;
using System.Reflection;
using MongoDB.Bson;

namespace MongoZen.Bson;

/// <summary>
/// How MongoZen's serializers (both the reflection tier and the source generator share this
/// vocabulary) treat a .NET type. Every property, collection element and dictionary value is
/// classified exactly once with <see cref="BsonTypeModel.Classify"/>; anything that comes back
/// <see cref="Unsupported"/> fails loudly at serializer construction instead of being dropped or
/// written as a nested document of its public properties, which is what used to happen.
/// </summary>
public enum BsonTypeKind
{
    /// <summary>int, short, ushort, byte, sbyte, char: BSON Int32.</summary>
    Int32,
    /// <summary>long, uint, ulong: BSON Int64 (ulong is reinterpreted, same as the official driver's default).</summary>
    Int64,
    /// <summary>double, float: BSON Double.</summary>
    Double,
    Boolean,
    String,
    ObjectId,
    DateTime,
    Guid,
    Decimal128,
    /// <summary>Any enum; written as its underlying integral type.</summary>
    Enum,
    /// <summary><c>Nullable&lt;T&gt;</c> of any supported value kind; BSON Null when it has no value.</summary>
    Nullable,
    /// <summary><c>byte[]</c>: BSON Binary (subtype 0), never an array of integers.</summary>
    ByteArray,
    /// <summary>Arrays and anything implementing <c>IEnumerable&lt;T&gt;</c> (except string, byte[] and dictionaries): BSON Array.</summary>
    Collection,
    /// <summary>Anything implementing <c>IDictionary&lt;string,V&gt;</c>/<c>IReadOnlyDictionary&lt;string,V&gt;</c> or enumerating <c>KeyValuePair&lt;string,V&gt;</c>: BSON Document keyed by the dictionary keys.</summary>
    Dictionary,
    /// <summary>A user class or struct serialized property-by-property as a nested BSON Document.</summary>
    Document,
    /// <summary>A user class that carries MongoDB driver serialization attributes the dynamic serializer does not understand; round-tripped through the official driver's serializer (the "Tier 3" compatibility valve).</summary>
    DriverBridged,
    Unsupported,
}

public static class BsonTypeModel
{
    private static readonly string[] SupportedKindsForMessage =
    [
        "int, long, short, ushort, byte, sbyte, char, uint, ulong", "double, float", "bool", "string",
        "ObjectId", "DateTime", "Guid", "decimal", "enums", "Nullable<T> of those", "byte[]",
        "arrays / IEnumerable<T> of a supported type", "IDictionary<string, V> of a supported V",
        "your own classes and structs (serialized as nested documents)",
    ];

    /// <summary>
    /// Classifies <paramref name="type"/>. For <see cref="BsonTypeKind.Nullable"/>, <see cref="BsonTypeKind.Collection"/>
    /// and <see cref="BsonTypeKind.Enum"/>, <paramref name="inner"/> is the underlying/element type;
    /// for <see cref="BsonTypeKind.Dictionary"/> it is the value type.
    /// </summary>
    public static BsonTypeKind Classify(Type type, out Type? inner)
    {
        inner = null;

        if (type == typeof(string)) return BsonTypeKind.String;
        if (type == typeof(byte[])) return BsonTypeKind.ByteArray;
        if (type == typeof(bool)) return BsonTypeKind.Boolean;
        if (type == typeof(int) || type == typeof(short) || type == typeof(ushort) || type == typeof(byte) || type == typeof(sbyte) || type == typeof(char)) return BsonTypeKind.Int32;
        if (type == typeof(long) || type == typeof(uint) || type == typeof(ulong)) return BsonTypeKind.Int64;
        if (type == typeof(double) || type == typeof(float)) return BsonTypeKind.Double;
        if (type == typeof(ObjectId)) return BsonTypeKind.ObjectId;
        if (type == typeof(DateTime)) return BsonTypeKind.DateTime;
        if (type == typeof(Guid)) return BsonTypeKind.Guid;
        if (type == typeof(decimal)) return BsonTypeKind.Decimal128;

        if (type.IsEnum)
        {
            inner = Enum.GetUnderlyingType(type);
            // Only integral underlying types that fit the Int32/Int64 pair are representable.
            return inner == typeof(bool) ? BsonTypeKind.Unsupported : BsonTypeKind.Enum;
        }

        var nullableInner = System.Nullable.GetUnderlyingType(type);
        if (nullableInner != null)
        {
            inner = nullableInner;
            var innerKind = Classify(nullableInner, out _);
            return innerKind is BsonTypeKind.Unsupported or BsonTypeKind.String or BsonTypeKind.ByteArray
                   or BsonTypeKind.Collection or BsonTypeKind.Dictionary or BsonTypeKind.DriverBridged or BsonTypeKind.Nullable
                ? BsonTypeKind.Unsupported
                : BsonTypeKind.Nullable;
        }

        if (type.IsPointer || type.IsByRefLike || type == typeof(object) || type == typeof(ValueType) || type.IsInterface && type == typeof(IEnumerable))
        {
            return BsonTypeKind.Unsupported;
        }

        if (TryGetDictionaryValueType(type, out var valueType, out var hasNonStringKey))
        {
            inner = valueType;
            return BsonTypeKind.Dictionary;
        }

        if (hasNonStringKey)
        {
            // BSON document keys are strings; a Dictionary<int, V> has no faithful representation and
            // must not fall through to "collection of KeyValuePair" (which would then fail on the pair).
            return BsonTypeKind.Unsupported;
        }

        if (TryGetCollectionElementType(type, out var elementType))
        {
            inner = elementType;
            return BsonTypeKind.Collection;
        }

        if (IsFrameworkType(type))
        {
            // TimeSpan, DateTimeOffset, DateOnly, TimeOnly, Uri, Version, BsonDocument, Stream, ...:
            // these have no sane property-by-property representation. Rather than guess one (and
            // silently disagree with the official driver's default for the same type), refuse until a
            // representation is chosen deliberately.
            return BsonTypeKind.Unsupported;
        }

        if (type.IsClass || type.IsValueType)
        {
            return RequiresDriverBridge(type) ? BsonTypeKind.DriverBridged : BsonTypeKind.Document;
        }

        return BsonTypeKind.Unsupported;
    }

    public static bool IsScalar(BsonTypeKind kind) => kind is BsonTypeKind.Int32 or BsonTypeKind.Int64 or BsonTypeKind.Double
        or BsonTypeKind.Boolean or BsonTypeKind.String or BsonTypeKind.ObjectId or BsonTypeKind.DateTime or BsonTypeKind.Guid
        or BsonTypeKind.Decimal128 or BsonTypeKind.Enum or BsonTypeKind.Nullable or BsonTypeKind.ByteArray;

    public static bool IsReferenceLike(Type type) => !type.IsValueType || System.Nullable.GetUnderlyingType(type) != null;

    /// <summary>
    /// Walks <paramref name="rootType"/>'s serializable graph and throws <see cref="NotSupportedException"/>
    /// naming the first offending member (e.g. <c>Order.Lines[].Discount (System.TimeSpan)</c>). Called
    /// once per entity type before any serializer is compiled, so the failure happens at first use of the
    /// type rather than as silent data loss on write.
    /// </summary>
    public static void EnsureSupported(Type rootType)
    {
        var visited = new HashSet<Type>();
        Validate(rootType, rootType.Name, visited, isRoot: true);
    }

    private static void Validate(Type type, string path, HashSet<Type> visited, bool isRoot)
    {
        var kind = Classify(type, out var inner);
        switch (kind)
        {
            case BsonTypeKind.Unsupported:
                throw Unsupported(path, type);

            case BsonTypeKind.Nullable:
            case BsonTypeKind.Enum:
                return;

            case BsonTypeKind.Collection:
                Validate(inner!, path + "[]", visited, isRoot: false);
                return;

            case BsonTypeKind.Dictionary:
                Validate(inner!, path + "[key]", visited, isRoot: false);
                return;

            case BsonTypeKind.Document:
                if (!visited.Add(type))
                {
                    return; // recursive type (tree nodes etc.) — already validated or in progress
                }

                if (!isRoot && !HasParameterlessConstructor(type))
                {
                    throw new NotSupportedException(
                        $"MongoZen cannot deserialize '{path}' ({type.FullName}): the type has no public parameterless constructor.");
                }

                foreach (var prop in GetSerializableProperties(type))
                {
                    Validate(prop.PropertyType, path + "." + prop.Name, visited, isRoot: false);
                }
                return;

            default:
                return;
        }
    }

    public static NotSupportedException Unsupported(string path, Type type) => new(
        $"MongoZen cannot serialize '{path}' ({type.FullName}). Supported types: {string.Join("; ", SupportedKindsForMessage)}. " +
        "Mark the member [BsonIgnore], change its type, or register an IBlittableConverter<T> for it via BlittableConverter<T>.Register.");

    /// <summary>
    /// Public, readable *and writable* (any setter accessibility, init included), non-indexer,
    /// non-[BsonIgnore] instance properties, in declaration order. This is the single definition of
    /// "what gets serialized" for the reflection tier and mirrors the source generator. Get-only
    /// (computed) properties are skipped: they could never be read back, and serializing them turned
    /// every change of a computed value into a spurious <c>$set</c>.
    /// </summary>
    public static IEnumerable<PropertyInfo> GetSerializableProperties(Type type)
    {
        foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!prop.CanRead || prop.SetMethod == null || prop.GetIndexParameters().Length > 0)
            {
                continue;
            }

            if (prop.GetCustomAttribute<MongoDB.Bson.Serialization.Attributes.BsonIgnoreAttribute>() != null)
            {
                continue;
            }

            if (prop.PropertyType.IsByRefLike || prop.PropertyType.IsPointer)
            {
                continue;
            }

            yield return prop;
        }
    }

    public static string GetElementName(PropertyInfo prop)
    {
        var idAttr = prop.GetCustomAttribute<MongoDB.Bson.Serialization.Attributes.BsonIdAttribute>();
        if (idAttr != null || string.Equals(prop.Name, "Id", StringComparison.OrdinalIgnoreCase))
        {
            // Case-insensitive on purpose: DefaultIdConvention resolves the id property the same way,
            // and the two must agree or a `public int ID` entity is stored under a server-generated _id
            // and can never be loaded by its own id.
            return "_id";
        }

        if (prop.GetCustomAttribute<ConcurrencyCheckAttribute>() != null)
        {
            return "_etag";
        }

        var elementAttr = prop.GetCustomAttribute<MongoDB.Bson.Serialization.Attributes.BsonElementAttribute>();
        return elementAttr?.ElementName ?? prop.Name;
    }

    public static bool TryGetDictionaryValueType(Type type, out Type valueType) =>
        TryGetDictionaryValueType(type, out valueType, out _);

    /// <summary><paramref name="hasNonStringKey"/> is set when the type is dictionary-shaped but keyed by something other than string.</summary>
    public static bool TryGetDictionaryValueType(Type type, out Type valueType, out bool hasNonStringKey)
    {
        hasNonStringKey = false;
        foreach (var iface in InterfacesIncludingSelf(type))
        {
            if (!iface.IsGenericType)
            {
                continue;
            }

            var def = iface.GetGenericTypeDefinition();
            if (def == typeof(IDictionary<,>) || def == typeof(IReadOnlyDictionary<,>))
            {
                var args = iface.GetGenericArguments();
                if (args[0] == typeof(string))
                {
                    valueType = args[1];
                    return true;
                }

                hasNonStringKey = true;
            }
        }

        valueType = null!;
        return false;
    }

    public static bool TryGetCollectionElementType(Type type, out Type elementType)
    {
        if (type == typeof(string) || type == typeof(byte[]))
        {
            elementType = null!;
            return false;
        }

        if (type.IsArray)
        {
            if (type.GetArrayRank() != 1)
            {
                elementType = null!;
                return false;
            }

            elementType = type.GetElementType()!;
            return true;
        }

        foreach (var iface in InterfacesIncludingSelf(type))
        {
            if (iface.IsGenericType && iface.GetGenericTypeDefinition() == typeof(IEnumerable<>))
            {
                elementType = iface.GetGenericArguments()[0];
                return true;
            }
        }

        elementType = null!;
        return false;
    }

    private static IEnumerable<Type> InterfacesIncludingSelf(Type type)
    {
        if (type.IsInterface)
        {
            yield return type;
        }

        foreach (var iface in type.GetInterfaces())
        {
            yield return iface;
        }
    }

    private static bool IsFrameworkType(Type type)
    {
        var ns = type.Namespace;
        if (ns == null)
        {
            return false;
        }

        return ns == "System" || ns.StartsWith("System.", StringComparison.Ordinal)
            || ns == "MongoDB" || ns.StartsWith("MongoDB.", StringComparison.Ordinal)
            || ns == "Microsoft" || ns.StartsWith("Microsoft.", StringComparison.Ordinal);
    }

    private static bool HasParameterlessConstructor(Type type) =>
        type.IsValueType || type.GetConstructor(BindingFlags.Public | BindingFlags.Instance, Type.EmptyTypes) != null;

    /// <summary>
    /// A nested type goes through the official driver's serializer when it carries driver attributes
    /// the dynamic serializer does not implement (representation, serializer, discriminator, defaults,
    /// ...). Naming attributes (BsonElement/BsonId/BsonIgnore) are understood natively and don't count.
    /// </summary>
    private static bool RequiresDriverBridge(Type type)
    {
        if (HasBridgeAttribute(CustomAttributeExtensions.GetCustomAttributes(type, inherit: true)))
        {
            return true;
        }

        foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (HasBridgeAttribute(CustomAttributeExtensions.GetCustomAttributes(prop, inherit: true)))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasBridgeAttribute(IEnumerable<Attribute> attributes)
    {
        foreach (var attribute in attributes)
        {
            var attrType = attribute.GetType();
            if (!(attrType.Namespace?.StartsWith("MongoDB.Bson.Serialization.Attributes", StringComparison.Ordinal) ?? false))
            {
                continue;
            }

            if (attribute is MongoDB.Bson.Serialization.Attributes.BsonElementAttribute
                or MongoDB.Bson.Serialization.Attributes.BsonIdAttribute
                or MongoDB.Bson.Serialization.Attributes.BsonIgnoreAttribute
                or MongoDB.Bson.Serialization.Attributes.BsonIgnoreExtraElementsAttribute
                or MongoDB.Bson.Serialization.Attributes.BsonNoIdAttribute)
            {
                // Naming/shape attributes the dynamic serializer honours (or that are moot for it:
                // extra elements are always ignored on read).
                continue;
            }

            return true;
        }

        return false;
    }
}
