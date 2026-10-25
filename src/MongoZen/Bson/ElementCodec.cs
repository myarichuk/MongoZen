using System.Linq.Expressions;
using System.Reflection;
using MongoDB.Bson;
using SharpArena.Allocators;

namespace MongoZen.Bson;

/// <summary>
/// Compiled read/write/compare delegates for a single value type <typeparamref name="T"/> in any BSON
/// position: a document property, an array element or a dictionary value. Every position used to have
/// its own hand-written type switch (and they disagreed: a <c>List&lt;Guid&gt;</c> property serialized its
/// elements as empty documents while a <c>Guid</c> property worked), so this is now the only place that
/// knows how a .NET type maps to BSON. Unsupported types throw a descriptive
/// <see cref="NotSupportedException"/> on first use instead of being silently dropped.
/// </summary>
public static unsafe class ElementCodec<T>
{
    public delegate void WriteElementDelegate(ref ArenaBsonWriter writer, ReadOnlySpan<char> name, T value);

    /// <summary><paramref name="p"/> points at the element's data (after type byte and name).</summary>
    public delegate T ReadValueDelegate(nint p, BlittableBsonConstants.BsonType type, int length, ArenaAllocator arena);

    /// <summary>True when <paramref name="value"/> would serialize to the BSON value at <paramref name="p"/>.</summary>
    public delegate bool EqualsSnapshotDelegate(T value, nint p, BlittableBsonConstants.BsonType type, int length, ArenaAllocator arena);

    public static readonly BsonTypeKind Kind;
    public static readonly WriteElementDelegate Write;
    public static readonly ReadValueDelegate Read;
    public static readonly EqualsSnapshotDelegate EqualsSnapshot;

    static ElementCodec()
    {
        Kind = BsonTypeModel.Classify(typeof(T), out _);
        try
        {
            BsonTypeModel.EnsureSupported(typeof(T));
            Write = ElementCodecCompiler.CompileWrite<T>();
            Read = ElementCodecCompiler.CompileRead<T>();
            EqualsSnapshot = ElementCodecCompiler.CompileEquals<T>();
        }
        catch (NotSupportedException ex)
        {
            // Surface the real message at the call site instead of a TypeInitializationException.
            Write = (ref ArenaBsonWriter _, ReadOnlySpan<char> _, T _) => throw ex;
            Read = (_, _, _, _) => throw ex;
            EqualsSnapshot = (_, _, _, _, _) => throw ex;
        }
    }

    public static T ReadAt(byte* p, BlittableBsonConstants.BsonType type, int length, ArenaAllocator arena) =>
        Read((nint)p, type, length, arena);

    public static bool EqualsAt(T value, byte* p, BlittableBsonConstants.BsonType type, int length, ArenaAllocator arena) =>
        EqualsSnapshot(value, (nint)p, type, length, arena);

    /// <summary>Compares <paramref name="value"/> with the element at <paramref name="offset"/> of <paramref name="doc"/> (used by generated code).</summary>
    public static bool EqualsAt(T value, BlittableBsonDocument doc, int offset, ArenaAllocator arena)
    {
        var p = doc.GetElementData(offset, out var type, out var length);
        return EqualsSnapshot(value, (nint)p, type, length, arena);
    }
}

/// <summary>
/// Expression-tree emitter behind <see cref="ElementCodec{T}"/> and <see cref="DynamicBlittableSerializer{T}"/>.
/// Expression trees cannot take pointer-typed parameters, so compiled code sees element data as
/// <see cref="nint"/> and calls into the small C# helpers in this file / <see cref="BsonValueReader"/>.
/// </summary>
internal static class ElementCodecCompiler
{
    private static readonly MethodInfo AsSpanMethod = typeof(MemoryExtensions).GetMethod(nameof(MemoryExtensions.AsSpan), [typeof(string)])!;
    private static readonly ConstructorInfo ReadOnlyByteSpanCtor = typeof(ReadOnlySpan<byte>).GetConstructor([typeof(byte[])])!;
    private static readonly Type BsonTypeEnum = typeof(BlittableBsonConstants.BsonType);
    private static readonly ConstantExpression NullBsonType = Expression.Constant(BlittableBsonConstants.BsonType.Null);
    private static readonly ConstantExpression DocumentBsonType = Expression.Constant(BlittableBsonConstants.BsonType.Document);
    private static readonly ConstantExpression ArrayBsonType = Expression.Constant(BlittableBsonConstants.BsonType.Array);

    private static MethodInfo Writer(string name, params Type[] args) => typeof(ArenaBsonWriter).GetMethod(name, args)
        ?? throw new MissingMethodException(nameof(ArenaBsonWriter), name);

    private static MethodInfo Reader(string name) => typeof(BsonValueReader).GetMethod(name, [typeof(nint), BsonTypeEnum])
        ?? throw new MissingMethodException(nameof(BsonValueReader), name);

    private static readonly Type RosChar = typeof(ReadOnlySpan<char>);

    public static ElementCodec<T>.WriteElementDelegate CompileWrite<T>()
    {
        var writer = Expression.Parameter(typeof(ArenaBsonWriter).MakeByRefType(), "writer");
        var name = Expression.Parameter(RosChar, "name");
        var value = Expression.Parameter(typeof(T), "value");
        var body = EmitWrite(writer, name, value, typeof(T));
        return Expression.Lambda<ElementCodec<T>.WriteElementDelegate>(body, writer, name, value).Compile();
    }

    public static ElementCodec<T>.ReadValueDelegate CompileRead<T>()
    {
        var p = Expression.Parameter(typeof(nint), "p");
        var type = Expression.Parameter(BsonTypeEnum, "type");
        var length = Expression.Parameter(typeof(int), "length");
        var arena = Expression.Parameter(typeof(ArenaAllocator), "arena");
        var body = EmitRead(p, type, length, arena, typeof(T));
        return Expression.Lambda<ElementCodec<T>.ReadValueDelegate>(body, p, type, length, arena).Compile();
    }

    public static ElementCodec<T>.EqualsSnapshotDelegate CompileEquals<T>()
    {
        var value = Expression.Parameter(typeof(T), "value");
        var p = Expression.Parameter(typeof(nint), "p");
        var type = Expression.Parameter(BsonTypeEnum, "type");
        var length = Expression.Parameter(typeof(int), "length");
        var arena = Expression.Parameter(typeof(ArenaAllocator), "arena");
        var body = EmitEquals(value, p, type, length, arena, typeof(T));

        // A snapshot written by something else (the official driver, another language) can hold a
        // value this property's type cannot represent (a double in an int field, an out-of-range
        // Int64). That is a difference, not a crash: report "changed" so the next save normalizes it.
        var guarded = Expression.TryCatch(
            body,
            Expression.Catch(typeof(InvalidCastException), Expression.Constant(false)),
            Expression.Catch(typeof(OverflowException), Expression.Constant(false)),
            Expression.Catch(typeof(InvalidOperationException), Expression.Constant(false)));

        return Expression.Lambda<ElementCodec<T>.EqualsSnapshotDelegate>(guarded, value, p, type, length, arena).Compile();
    }

    /// <summary>Span over a constant element name; allocation-free at runtime.</summary>
    public static Expression ConstantName(string elementName) => Expression.Call(AsSpanMethod, Expression.Constant(elementName));

    // ---------------------------------------------------------------- write

    /// <summary>Emits "write element <paramref name="name"/> with <paramref name="value"/>" for <paramref name="type"/>, nulls included.</summary>
    public static Expression EmitWrite(ParameterExpression writer, Expression name, Expression value, Type type)
    {
        var kind = BsonTypeModel.Classify(type, out var inner);
        switch (kind)
        {
            case BsonTypeKind.Int32:
                return Expression.Call(writer, Writer(nameof(ArenaBsonWriter.WriteInt32), RosChar, typeof(int)), name, Expression.Convert(value, typeof(int)));
            case BsonTypeKind.Int64:
                return Expression.Call(writer, Writer(nameof(ArenaBsonWriter.WriteInt64), RosChar, typeof(long)), name, Expression.Convert(value, typeof(long)));
            case BsonTypeKind.Double:
                return Expression.Call(writer, Writer(nameof(ArenaBsonWriter.WriteDouble), RosChar, typeof(double)), name, Expression.Convert(value, typeof(double)));
            case BsonTypeKind.Boolean:
                return Expression.Call(writer, Writer(nameof(ArenaBsonWriter.WriteBoolean), RosChar, typeof(bool)), name, value);
            case BsonTypeKind.ObjectId:
                return Expression.Call(writer, Writer(nameof(ArenaBsonWriter.WriteObjectId), RosChar, typeof(ObjectId)), name, value);
            case BsonTypeKind.DateTime:
                return Expression.Call(writer, Writer(nameof(ArenaBsonWriter.WriteDateTime), RosChar, typeof(DateTime)), name, value);
            case BsonTypeKind.Guid:
                return Expression.Call(writer, Writer(nameof(ArenaBsonWriter.WriteGuid), RosChar, typeof(Guid)), name, value);
            case BsonTypeKind.Decimal128:
                return Expression.Call(writer, Writer(nameof(ArenaBsonWriter.WriteDecimal128), RosChar, typeof(decimal)), name, value);
            case BsonTypeKind.String:
                return NullOr(value, type, WriteNull(writer, name),
                    Expression.Call(writer, Writer(nameof(ArenaBsonWriter.WriteString), RosChar, RosChar), name, Expression.Call(AsSpanMethod, value)));
            case BsonTypeKind.Enum:
                return EmitWrite(writer, name, Expression.Convert(value, inner!), inner!);
            case BsonTypeKind.Nullable:
                return Expression.IfThenElse(
                    Expression.Property(value, "HasValue"),
                    EmitWrite(writer, name, Expression.Property(value, "Value"), inner!),
                    WriteNull(writer, name));
            case BsonTypeKind.ByteArray:
                return NullOr(value, type, WriteNull(writer, name),
                    Expression.Call(writer, Writer(nameof(ArenaBsonWriter.WriteBinary), RosChar, typeof(ReadOnlySpan<byte>), typeof(byte)),
                        name, Expression.New(ReadOnlyByteSpanCtor, value), Expression.Constant((byte)0)));
            case BsonTypeKind.Collection:
            {
                var helper = typeof(CollectionHelper<>).MakeGenericType(inner!);
                var method = helper.GetMethod(nameof(CollectionHelper<int>.WriteArray))!;
                var enumerable = typeof(IEnumerable<>).MakeGenericType(inner!);
                return NullOr(value, type, WriteNull(writer, name),
                    Expression.Call(method, writer, name, Expression.Convert(value, enumerable)));
            }
            case BsonTypeKind.Dictionary:
            {
                var helper = typeof(DictionaryHelper<>).MakeGenericType(inner!);
                var method = helper.GetMethod(nameof(DictionaryHelper<int>.WriteDictionary))!;
                var enumerable = typeof(IEnumerable<>).MakeGenericType(typeof(KeyValuePair<,>).MakeGenericType(typeof(string), inner!));
                return NullOr(value, type, WriteNull(writer, name),
                    Expression.Call(method, writer, name, Expression.Convert(value, enumerable)));
            }
            case BsonTypeKind.Document:
            {
                if (HasCustomConverter(type))
                {
                    return EmitConverterWrite(writer, name, value, type);
                }

                var serializer = typeof(DynamicBlittableSerializer<>).MakeGenericType(type);
                var field = serializer.GetField(nameof(DynamicBlittableSerializer<int>.SerializeDelegate))!;
                var write = Expression.Block(
                    Expression.Call(writer, Writer(nameof(ArenaBsonWriter.WriteName), RosChar, BsonTypeEnum), name, DocumentBsonType),
                    Expression.Invoke(Expression.Field(null, field), writer, value));
                return NullOr(value, type, WriteNull(writer, name), write);
            }
            case BsonTypeKind.DriverBridged:
                return EmitConverterWrite(writer, name, value, type);
            default:
                throw BsonTypeModel.Unsupported(type.Name, type);
        }
    }

    private static Expression EmitConverterWrite(ParameterExpression writer, Expression name, Expression value, Type type)
    {
        var converterType = typeof(BlittableConverter<>).MakeGenericType(type);
        var instance = Expression.Property(null, converterType.GetProperty(nameof(BlittableConverter<int>.Instance))!);
        var ifaceType = typeof(IBlittableConverter<>).MakeGenericType(type);
        var write = Expression.Block(
            Expression.Call(writer, Writer(nameof(ArenaBsonWriter.WriteName), RosChar, BsonTypeEnum), name,
                Expression.Property(instance, ifaceType.GetProperty(nameof(IBlittableConverter<int>.BsonType))!)),
            Expression.Call(instance, ifaceType.GetMethod(nameof(IBlittableConverter<int>.Write))!, writer, value));
        return NullOr(value, type, WriteNull(writer, name), write);
    }

    private static Expression WriteNull(ParameterExpression writer, Expression name) =>
        Expression.Call(writer, Writer(nameof(ArenaBsonWriter.WriteNull), RosChar), name);

    private static Expression NullOr(Expression value, Type type, Expression whenNull, Expression otherwise) =>
        type.IsValueType
            ? otherwise
            : Expression.IfThenElse(Expression.Equal(value, Expression.Constant(null, type)), whenNull, otherwise);

    // ---------------------------------------------------------------- read

    /// <summary>Emits an expression of type <paramref name="type"/> reading the element data at <paramref name="p"/>.</summary>
    public static Expression EmitRead(Expression p, Expression type, Expression length, Expression arena, Type targetType)
    {
        var kind = BsonTypeModel.Classify(targetType, out var inner);
        var isNull = Expression.Equal(type, NullBsonType);

        switch (kind)
        {
            case BsonTypeKind.Int32:
                return Expression.Convert(Expression.Call(Reader(nameof(BsonValueReader.ReadInt32)), p, type), targetType);
            case BsonTypeKind.Int64:
                return Expression.Convert(Expression.Call(Reader(nameof(BsonValueReader.ReadInt64)), p, type), targetType);
            case BsonTypeKind.Double:
                return Expression.Convert(Expression.Call(Reader(nameof(BsonValueReader.ReadDouble)), p, type), targetType);
            case BsonTypeKind.Boolean:
                return Expression.Call(Reader(nameof(BsonValueReader.ReadBoolean)), p, type);
            case BsonTypeKind.ObjectId:
                return Expression.Call(Reader(nameof(BsonValueReader.ReadObjectId)), p, type);
            case BsonTypeKind.DateTime:
                return Expression.Call(Reader(nameof(BsonValueReader.ReadDateTime)), p, type);
            case BsonTypeKind.Guid:
                return Expression.Call(Reader(nameof(BsonValueReader.ReadGuid)), p, type);
            case BsonTypeKind.Decimal128:
                return Expression.Call(Reader(nameof(BsonValueReader.ReadDecimal128)), p, type);
            case BsonTypeKind.String:
                return Expression.Condition(isNull, Expression.Constant(null, typeof(string)), Expression.Call(Reader(nameof(BsonValueReader.ReadString)), p, type));
            case BsonTypeKind.Enum:
                return Expression.Convert(EmitRead(p, type, length, arena, inner!), targetType);
            case BsonTypeKind.Nullable:
                return Expression.Condition(isNull, Expression.Default(targetType), Expression.Convert(EmitRead(p, type, length, arena, inner!), targetType));
            case BsonTypeKind.ByteArray:
                return Expression.Condition(isNull, Expression.Constant(null, typeof(byte[])), Expression.Call(Reader(nameof(BsonValueReader.ReadBinary)), p, type));
            case BsonTypeKind.Collection:
            {
                var helper = typeof(CollectionHelper<>).MakeGenericType(inner!);
                var readArray = helper.GetMethod(nameof(CollectionHelper<int>.ReadArrayRaw))!;
                var array = Expression.Call(readArray, p, type, length, arena);
                return Expression.Condition(isNull, Expression.Default(targetType), ConvertCollection(array, targetType, inner!));
            }
            case BsonTypeKind.Dictionary:
            {
                var helper = typeof(DictionaryHelper<>).MakeGenericType(inner!);
                var readDict = helper.GetMethod(nameof(DictionaryHelper<int>.ReadDictionaryRaw))!;
                var dictionary = Expression.Call(readDict, p, type, length, arena);
                return Expression.Condition(isNull, Expression.Default(targetType), ConvertDictionary(dictionary, targetType, inner!));
            }
            case BsonTypeKind.Document:
            {
                if (HasCustomConverter(targetType))
                {
                    return EmitConverterRead(p, type, length, targetType, isNull);
                }

                var serializer = typeof(DynamicBlittableSerializer<>).MakeGenericType(targetType);
                var field = serializer.GetField(nameof(DynamicBlittableSerializer<int>.DeserializeDelegate))!;
                var doc = Expression.Call(typeof(ElementCodecHelpers).GetMethod(nameof(ElementCodecHelpers.ReadDocument))!, p, type, length, arena);
                return Expression.Condition(isNull, Expression.Default(targetType), Expression.Invoke(Expression.Field(null, field), doc, arena));
            }
            case BsonTypeKind.DriverBridged:
                return EmitConverterRead(p, type, length, targetType, isNull);
            default:
                throw BsonTypeModel.Unsupported(targetType.Name, targetType);
        }
    }

    private static Expression EmitConverterRead(Expression p, Expression type, Expression length, Type targetType, Expression isNull)
    {
        var helper = typeof(ElementCodecHelpers).GetMethod(nameof(ElementCodecHelpers.ReadViaConverter))!.MakeGenericMethod(targetType);
        var read = Expression.Call(helper, p, type, length);
        return targetType.IsValueType ? read : Expression.Condition(isNull, Expression.Default(targetType), read);
    }

    private static Expression ConvertCollection(Expression array, Type targetType, Type elementType)
    {
        var arrayType = elementType.MakeArrayType();
        if (targetType == arrayType)
        {
            return array;
        }

        if (targetType.IsAssignableFrom(arrayType))
        {
            // IEnumerable<T>, IList<T>, IReadOnlyList<T>, ICollection<T>, IReadOnlyCollection<T>: an array satisfies all of them.
            return Expression.Convert(array, targetType);
        }

        var enumerableCtor = targetType.GetConstructor([typeof(IEnumerable<>).MakeGenericType(elementType)]);
        if (enumerableCtor != null)
        {
            // List<T>, HashSet<T>, Collection<T>, ObservableCollection<T>, Queue<T>, Stack<T>, LinkedList<T>, SortedSet<T>, ...
            return Expression.New(enumerableCtor, array);
        }

        throw new NotSupportedException(
            $"MongoZen cannot deserialize into '{targetType.FullName}': it is not an array, not assignable from '{elementType.Name}[]', " +
            $"and has no public constructor taking IEnumerable<{elementType.Name}>.");
    }

    private static Expression ConvertDictionary(Expression dictionary, Type targetType, Type valueType)
    {
        var concreteType = typeof(Dictionary<,>).MakeGenericType(typeof(string), valueType);
        if (targetType.IsAssignableFrom(concreteType))
        {
            return targetType == concreteType ? dictionary : Expression.Convert(dictionary, targetType);
        }

        var ctor = targetType.GetConstructor([typeof(IDictionary<,>).MakeGenericType(typeof(string), valueType)])
            ?? targetType.GetConstructor([typeof(IEnumerable<>).MakeGenericType(typeof(KeyValuePair<,>).MakeGenericType(typeof(string), valueType))]);
        if (ctor != null)
        {
            return Expression.New(ctor, dictionary);
        }

        throw new NotSupportedException(
            $"MongoZen cannot deserialize into '{targetType.FullName}': it is not assignable from Dictionary<string, {valueType.Name}> " +
            $"and has no public constructor taking IDictionary<string, {valueType.Name}> or IEnumerable<KeyValuePair<string, {valueType.Name}>>.");
    }

    // ---------------------------------------------------------------- equals

    /// <summary>Emits a bool expression: does <paramref name="value"/> serialize to the BSON value at <paramref name="p"/>?</summary>
    public static Expression EmitEquals(Expression value, Expression p, Expression type, Expression length, Expression arena, Type valueType)
    {
        var kind = BsonTypeModel.Classify(valueType, out var inner);
        var isNull = Expression.Equal(type, NullBsonType);

        switch (kind)
        {
            case BsonTypeKind.Int32:
                return NotNullAnd(isNull, Expression.Equal(Expression.Convert(value, typeof(int)), Expression.Call(Reader(nameof(BsonValueReader.ReadInt32)), p, type)));
            case BsonTypeKind.Int64:
                return NotNullAnd(isNull, Expression.Equal(Expression.Convert(value, typeof(long)), Expression.Call(Reader(nameof(BsonValueReader.ReadInt64)), p, type)));
            case BsonTypeKind.Double:
                return NotNullAnd(isNull, Expression.Equal(Expression.Convert(value, typeof(double)), Expression.Call(Reader(nameof(BsonValueReader.ReadDouble)), p, type)));
            case BsonTypeKind.Boolean:
            case BsonTypeKind.ObjectId:
            case BsonTypeKind.Guid:
            case BsonTypeKind.Decimal128:
                return NotNullAnd(isNull, Expression.Equal(value, EmitRead(p, type, length, arena, valueType)));
            case BsonTypeKind.DateTime:
                return NotNullAnd(isNull, Expression.Call(typeof(BsonDateTimeUtil).GetMethod(nameof(BsonDateTimeUtil.Equal), [typeof(DateTime), typeof(DateTime)])!,
                    value, Expression.Call(Reader(nameof(BsonValueReader.ReadDateTime)), p, type)));
            case BsonTypeKind.String:
                return Expression.Condition(isNull,
                    Expression.Equal(value, Expression.Constant(null, typeof(string))),
                    Expression.Equal(value, Expression.Call(Reader(nameof(BsonValueReader.ReadString)), p, type)));
            case BsonTypeKind.Enum:
                return EmitEquals(Expression.Convert(value, inner!), p, type, length, arena, inner!);
            case BsonTypeKind.Nullable:
            {
                var hasValue = Expression.Property(value, "HasValue");
                return Expression.Condition(isNull,
                    Expression.Not(hasValue),
                    Expression.AndAlso(hasValue, EmitEquals(Expression.Property(value, "Value"), p, type, length, arena, inner!)));
            }
            case BsonTypeKind.ByteArray:
                return Expression.Call(typeof(ElementCodecHelpers).GetMethod(nameof(ElementCodecHelpers.BinaryEquals))!, value, p, type);
            case BsonTypeKind.Collection:
            {
                var helper = typeof(CollectionHelper<>).MakeGenericType(inner!);
                var method = helper.GetMethod(nameof(CollectionHelper<int>.EqualsSnapshotRaw))!;
                var enumerable = typeof(IEnumerable<>).MakeGenericType(inner!);
                return Expression.Call(method, Expression.Convert(value, enumerable), p, type, length, arena);
            }
            case BsonTypeKind.Dictionary:
            {
                var helper = typeof(DictionaryHelper<>).MakeGenericType(inner!);
                var method = helper.GetMethod(nameof(DictionaryHelper<int>.EqualsSnapshotRaw))!;
                var enumerable = typeof(IEnumerable<>).MakeGenericType(typeof(KeyValuePair<,>).MakeGenericType(typeof(string), inner!));
                return Expression.Call(method, Expression.Convert(value, enumerable), p, type, length, arena);
            }
            case BsonTypeKind.Document:
            case BsonTypeKind.DriverBridged:
            {
                var method = typeof(ElementCodecHelpers).GetMethod(nameof(ElementCodecHelpers.ValueBytesEqual))!.MakeGenericMethod(valueType);
                return Expression.Call(method, value, p, type, length, arena);
            }
            default:
                throw BsonTypeModel.Unsupported(valueType.Name, valueType);
        }
    }

    private static Expression NotNullAnd(Expression isNull, Expression comparison) =>
        Expression.AndAlso(Expression.Not(isNull), comparison);

    private static bool HasCustomConverter(Type type)
    {
        var converterType = typeof(BlittableConverter<>).MakeGenericType(type);
        return (bool)converterType.GetProperty(nameof(BlittableConverter<int>.HasCustom))!.GetValue(null)!;
    }
}

/// <summary>Pointer-touching helpers called from compiled expression trees (which cannot use pointer types directly).</summary>
public static unsafe class ElementCodecHelpers
{
    public static BlittableBsonDocument ReadDocument(nint p, BlittableBsonConstants.BsonType type, int length, ArenaAllocator arena)
    {
        if (type != BlittableBsonConstants.BsonType.Document)
        {
            throw new InvalidCastException($"Cannot cast {type} to Document");
        }

        return ArenaBsonReader.ReadInPlace((byte*)p, length, arena);
    }

    public static T ReadViaConverter<T>(nint p, BlittableBsonConstants.BsonType type, int length) =>
        BlittableConverter<T>.Instance.Read((byte*)p, type, length);

    public static bool BinaryEquals(byte[]? value, nint p, BlittableBsonConstants.BsonType type)
    {
        if (type == BlittableBsonConstants.BsonType.Null)
        {
            return value == null;
        }

        if (value == null || type != BlittableBsonConstants.BsonType.Binary)
        {
            return false;
        }

        return value.AsSpan().SequenceEqual(BsonValueReader.ReadBinarySpan((byte*)p, type));
    }

    /// <summary>
    /// Structural equality for nested documents and driver-bridged values: serialize
    /// <paramref name="value"/> the way we would on write and compare the bytes. Used for array elements
    /// and dictionary values; top-level nested properties get a field-by-field diff instead.
    /// </summary>
    public static bool ValueBytesEqual<T>(T value, nint p, BlittableBsonConstants.BsonType type, int length, ArenaAllocator arena)
    {
        if (type == BlittableBsonConstants.BsonType.Null)
        {
            return value == null;
        }

        if (value == null)
        {
            return false;
        }

        var writer = new ArenaBsonWriter(arena);
        writer.WriteStartDocument();
        ElementCodec<T>.Write(ref writer, "v", value);
        writer.WriteEndDocument();
        var doc = writer.Commit(arena);

        if (!doc.TryGetElementOffset("v", out var offset))
        {
            return false;
        }

        var q = doc.GetElementData(offset, out var writtenType, out var writtenLength);
        return writtenType == type && writtenLength == length
            && new ReadOnlySpan<byte>(q, writtenLength).SequenceEqual(new ReadOnlySpan<byte>((byte*)p, length));
    }
}
