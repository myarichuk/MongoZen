using System.Linq.Expressions;
using System.Reflection;
using SharpArena.Allocators;
using MongoZen.ChangeTracking;

namespace MongoZen.Bson;

/// <summary>
/// Provides high-performance, dynamic BSON serialization for any POCO using compiled Expression Trees
/// (Tier 2). Types implementing <see cref="IBlittableDocument{T}"/> (source-generated, Tier 1) are
/// dispatched to their generated methods instead. Member handling is shared with
/// <see cref="ElementCodec{T}"/>, so a property, an array element and a dictionary value of the same
/// type are always serialized identically; an unsupported member type throws a descriptive
/// <see cref="NotSupportedException"/> on first use of the entity type instead of being silently dropped.
/// </summary>
public static class DynamicBlittableSerializer<T>
{
    public delegate void SerializeAction(ref ArenaBsonWriter writer, T value);
    public delegate void BuildUpdateAction(T entity, BlittableBsonDocument snapshot, ref ArenaUpdateDefinitionBuilder builder, ArenaAllocator arena, ReadOnlySpan<char> pathPrefix);
    public delegate void DeserializeIntoAction(BlittableBsonDocument doc, ArenaAllocator arena, T instance);

    public static readonly SerializeAction SerializeDelegate;
    public static readonly Func<BlittableBsonDocument, ArenaAllocator, T> DeserializeDelegate;
    public static readonly DeserializeIntoAction DeserializeIntoDelegate;
    public static readonly BuildUpdateAction BuildUpdateDelegate;

    static DynamicBlittableSerializer()
    {
        if (typeof(IBlittableDocument<T>).IsAssignableFrom(typeof(T)))
        {
            SerializeDelegate = CompileTier1Serialize();
            DeserializeDelegate = CompileTier1Deserialize();
            DeserializeIntoDelegate = CompileTier1DeserializeInto();
            BuildUpdateDelegate = CompileTier1BuildUpdate();
            return;
        }

        try
        {
            var kind = BsonTypeModel.Classify(typeof(T), out _);
            if (kind is not (BsonTypeKind.Document or BsonTypeKind.DriverBridged))
            {
                throw new NotSupportedException(
                    $"MongoZen can only use a class or struct with public properties as a document type; '{typeof(T).FullName}' is classified as {kind}.");
            }

            BsonTypeModel.EnsureSupported(typeof(T));
            SerializeDelegate = Emitter.CompileSerializer();
            DeserializeIntoDelegate = Emitter.CompileDeserializerInto();
            DeserializeDelegate = Emitter.CompileDeserializer(DeserializeIntoDelegate);
            BuildUpdateDelegate = Emitter.CompileUpdateBuilder();
        }
        catch (NotSupportedException ex)
        {
            // Rethrow the real message at the call site rather than a TypeInitializationException
            // whose InnerException the user has to dig for.
            SerializeDelegate = (ref ArenaBsonWriter _, T _) => throw ex;
            DeserializeDelegate = (_, _) => throw ex;
            DeserializeIntoDelegate = (_, _, _) => throw ex;
            BuildUpdateDelegate = (T _, BlittableBsonDocument _, ref ArenaUpdateDefinitionBuilder _, ArenaAllocator _, ReadOnlySpan<char> _) => throw ex;
        }
    }

    private static SerializeAction CompileTier1Serialize()
    {
        var writerParam = Expression.Parameter(typeof(ArenaBsonWriter).MakeByRefType(), "writer");
        var entityParam = Expression.Parameter(typeof(T), "entity");
        var method = typeof(T).GetMethod("Serialize", [typeof(ArenaBsonWriter).MakeByRefType(), typeof(T)])!;
        return Expression.Lambda<SerializeAction>(Expression.Call(null, method, writerParam, entityParam), writerParam, entityParam).Compile();
    }

    private static Func<BlittableBsonDocument, ArenaAllocator, T> CompileTier1Deserialize()
    {
        var docParam = Expression.Parameter(typeof(BlittableBsonDocument), "doc");
        var arenaParam = Expression.Parameter(typeof(ArenaAllocator), "arena");
        var method = typeof(T).GetMethod("Deserialize", [typeof(BlittableBsonDocument), typeof(ArenaAllocator)])!;
        return Expression.Lambda<Func<BlittableBsonDocument, ArenaAllocator, T>>(Expression.Call(null, method, docParam, arenaParam), docParam, arenaParam).Compile();
    }

    private static DeserializeIntoAction CompileTier1DeserializeInto()
    {
        var docParam = Expression.Parameter(typeof(BlittableBsonDocument), "doc");
        var arenaParam = Expression.Parameter(typeof(ArenaAllocator), "arena");
        var instanceParam = Expression.Parameter(typeof(T), "instance");
        var method = typeof(T).GetMethod("DeserializeInto", [typeof(BlittableBsonDocument), typeof(ArenaAllocator), typeof(T)]);
        if (method == null)
        {
            return Emitter.CompileDeserializerInto();
        }
        return Expression.Lambda<DeserializeIntoAction>(Expression.Call(null, method, docParam, arenaParam, instanceParam), docParam, arenaParam, instanceParam).Compile();
    }

    private static BuildUpdateAction CompileTier1BuildUpdate()
    {
        var entityParam = Expression.Parameter(typeof(T), "entity");
        var snapshotParam = Expression.Parameter(typeof(BlittableBsonDocument), "snapshot");
        var builderParam = Expression.Parameter(typeof(ArenaUpdateDefinitionBuilder).MakeByRefType(), "builder");
        var arenaParam = Expression.Parameter(typeof(ArenaAllocator), "arena");
        var prefixParam = Expression.Parameter(typeof(ReadOnlySpan<char>), "pathPrefix");
        var method = typeof(T).GetMethod("BuildUpdate", [typeof(T), typeof(BlittableBsonDocument), typeof(ArenaUpdateDefinitionBuilder).MakeByRefType(), typeof(ArenaAllocator), typeof(ReadOnlySpan<char>)])!;
        return Expression.Lambda<BuildUpdateAction>(Expression.Call(null, method, entityParam, snapshotParam, builderParam, arenaParam, prefixParam), entityParam, snapshotParam, builderParam, arenaParam, prefixParam).Compile();
    }

    private static class Emitter
    {
        private static readonly Type BsonTypeEnum = typeof(BlittableBsonConstants.BsonType);
        private static readonly MethodInfo WriteStartDocMethod = typeof(ArenaBsonWriter).GetMethod(nameof(ArenaBsonWriter.WriteStartDocument), Type.EmptyTypes)!;
        private static readonly MethodInfo WriteEndDocMethod = typeof(ArenaBsonWriter).GetMethod(nameof(ArenaBsonWriter.WriteEndDocument), Type.EmptyTypes)!;
        private static readonly MethodInfo TryGetElementOffsetMethod = typeof(BlittableBsonDocument).GetMethod(nameof(BlittableBsonDocument.TryGetElementOffset), [typeof(ReadOnlySpan<char>), typeof(int).MakeByRefType()])!;
        private static readonly MethodInfo GetElementDataPtrMethod = typeof(BlittableBsonDocument).GetMethod(nameof(BlittableBsonDocument.GetElementDataPtr))!;
        private static readonly MethodInfo ReadDocumentMethod = typeof(ElementCodecHelpers).GetMethod(nameof(ElementCodecHelpers.ReadDocument))!;
        private static readonly MethodInfo CombinePathMethod = typeof(ArenaUpdateDefinitionBuilder).GetMethod(nameof(ArenaUpdateDefinitionBuilder.CombinePath), [typeof(ReadOnlySpan<char>), typeof(string)])!;
        private static readonly MethodInfo SetNullMethod = typeof(ArenaUpdateDefinitionBuilder).GetMethod(nameof(ArenaUpdateDefinitionBuilder.SetNull), [typeof(ReadOnlySpan<char>)])!;
        private static readonly MethodInfo SetValueMethodDefinition = typeof(ArenaUpdateDefinitionBuilder).GetMethod(nameof(ArenaUpdateDefinitionBuilder.SetValue))!;
        private static readonly MethodInfo ValueBytesEqualDefinition = typeof(ElementCodecHelpers).GetMethod(nameof(ElementCodecHelpers.ValueBytesEqual))!;
        private static readonly ConstantExpression NullBsonType = Expression.Constant(BlittableBsonConstants.BsonType.Null);
        private static readonly ConstantExpression DocumentBsonType = Expression.Constant(BlittableBsonConstants.BsonType.Document);

        public static SerializeAction CompileSerializer()
        {
            var writerParam = Expression.Parameter(typeof(ArenaBsonWriter).MakeByRefType(), "writer");
            var objParam = Expression.Parameter(typeof(T), "obj");

            var body = new List<Expression> { Expression.Call(writerParam, WriteStartDocMethod) };
            foreach (var prop in BsonTypeModel.GetSerializableProperties(typeof(T)))
            {
                var name = ElementCodecCompiler.ConstantName(BsonTypeModel.GetElementName(prop));
                body.Add(ElementCodecCompiler.EmitWrite(writerParam, name, Expression.Property(objParam, prop), prop.PropertyType));
            }

            body.Add(Expression.Call(writerParam, WriteEndDocMethod));
            return Expression.Lambda<SerializeAction>(Expression.Block(body), writerParam, objParam).Compile();
        }

        public static Func<BlittableBsonDocument, ArenaAllocator, T> CompileDeserializer(DeserializeIntoAction into)
        {
            var docParam = Expression.Parameter(typeof(BlittableBsonDocument), "doc");
            var arenaParam = Expression.Parameter(typeof(ArenaAllocator), "arena");
            var objVar = Expression.Variable(typeof(T), "obj");

            var body = Expression.Block(
                [objVar],
                Expression.Assign(objVar, Expression.New(typeof(T))),
                Expression.Invoke(Expression.Constant(into), docParam, arenaParam, objVar),
                objVar);

            return Expression.Lambda<Func<BlittableBsonDocument, ArenaAllocator, T>>(body, docParam, arenaParam).Compile();
        }

        public static DeserializeIntoAction CompileDeserializerInto()
        {
            var docParam = Expression.Parameter(typeof(BlittableBsonDocument), "doc");
            var arenaParam = Expression.Parameter(typeof(ArenaAllocator), "arena");
            var objParam = Expression.Parameter(typeof(T), "obj");

            var body = new List<Expression>();
            foreach (var prop in BsonTypeModel.GetSerializableProperties(typeof(T)))
            {
                if (prop.SetMethod == null)
                {
                    continue;
                }

                body.Add(EmitPropertyRead(docParam, arenaParam, objParam, prop));
            }

            if (body.Count == 0)
            {
                body.Add(Expression.Empty());
            }

            return Expression.Lambda<DeserializeIntoAction>(Expression.Block(body), docParam, arenaParam, objParam).Compile();
        }

        private static Expression EmitPropertyRead(ParameterExpression doc, ParameterExpression arena, ParameterExpression obj, PropertyInfo prop)
        {
            var name = ElementCodecCompiler.ConstantName(BsonTypeModel.GetElementName(prop));
            var offset = Expression.Variable(typeof(int), "offset");
            var p = Expression.Variable(typeof(nint), "p");
            var type = Expression.Variable(BsonTypeEnum, "type");
            var length = Expression.Variable(typeof(int), "length");

            var read = ElementCodecCompiler.EmitRead(p, type, length, arena, prop.PropertyType);

            return Expression.Block(
                [offset, p, type, length],
                Expression.IfThen(
                    Expression.Call(doc, TryGetElementOffsetMethod, name, offset),
                    Expression.Block(
                        Expression.Assign(p, Expression.Call(doc, GetElementDataPtrMethod, offset, type, length)),
                        Expression.Assign(Expression.Property(obj, prop), read))));
        }

        public static BuildUpdateAction CompileUpdateBuilder()
        {
            var entityParam = Expression.Parameter(typeof(T), "entity");
            var snapshotParam = Expression.Parameter(typeof(BlittableBsonDocument), "snapshot");
            var builderParam = Expression.Parameter(typeof(ArenaUpdateDefinitionBuilder).MakeByRefType(), "builder");
            var arenaParam = Expression.Parameter(typeof(ArenaAllocator), "arena");
            var prefixParam = Expression.Parameter(typeof(ReadOnlySpan<char>), "pathPrefix");

            var inProgress = new HashSet<Type> { typeof(T) };
            var body = new List<Expression>();
            foreach (var prop in BsonTypeModel.GetSerializableProperties(typeof(T)))
            {
                body.Add(EmitPropertyDiff(entityParam, snapshotParam, builderParam, arenaParam, prop, prefixParam, inProgress));
            }

            if (body.Count == 0)
            {
                body.Add(Expression.Empty());
            }

            return Expression.Lambda<BuildUpdateAction>(Expression.Block(body), entityParam, snapshotParam, builderParam, arenaParam, prefixParam).Compile();
        }

        /// <summary>
        /// Diff of one property against the snapshot. Nested documents are diffed field by field
        /// (producing dotted paths); everything else is compared through <see cref="ElementCodec{T}"/>
        /// and replaced wholesale when different. Null on the entity side becomes <c>$set: null</c>, the
        /// same shape the insert path writes, so the refreshed snapshot and the database agree.
        /// </summary>
        private static Expression EmitPropertyDiff(Expression entity, Expression snapshot, ParameterExpression builder, ParameterExpression arena,
            PropertyInfo prop, Expression prefix, HashSet<Type> inProgress)
        {
            var type = prop.PropertyType;
            var kind = BsonTypeModel.Classify(type, out _);
            var value = Expression.Property(entity, prop);
            var elementName = BsonTypeModel.GetElementName(prop);
            var nameExpr = ElementCodecCompiler.ConstantName(elementName);

            var path = Expression.Variable(typeof(ReadOnlySpan<char>), "path");
            var offset = Expression.Variable(typeof(int), "offset");
            var p = Expression.Variable(typeof(nint), "p");
            var snapType = Expression.Variable(BsonTypeEnum, "snapType");
            var length = Expression.Variable(typeof(int), "length");

            var assignPath = Expression.Assign(path, Expression.Call(builder, CombinePathMethod, prefix, Expression.Constant(elementName)));
            var loadElement = Expression.Assign(p, Expression.Call(snapshot, GetElementDataPtrMethod, offset, snapType, length));
            var setValue = Expression.Call(builder, SetValueMethodDefinition.MakeGenericMethod(type), path, value);
            var setNull = Expression.Call(builder, SetNullMethod, path);
            var snapIsNull = Expression.Equal(snapType, NullBsonType);
            var isReferenceLike = BsonTypeModel.IsReferenceLike(type);
            var valueIsNull = isReferenceLike ? Expression.Equal(value, Expression.Constant(null, type)) : (Expression)Expression.Constant(false);

            Expression whenFound;
            if (kind == BsonTypeKind.Document && !type.IsValueType && inProgress.Add(type))
            {
                // Field-by-field diff of a nested class. Recursive types fall back to the whole-value
                // compare below once the type is already on the stack.
                var nestedSnap = Expression.Variable(typeof(BlittableBsonDocument), "nestedSnap");
                var nestedBody = new List<Expression>();
                foreach (var nestedProp in BsonTypeModel.GetSerializableProperties(type))
                {
                    nestedBody.Add(EmitPropertyDiff(value, nestedSnap, builder, arena, nestedProp, path, inProgress));
                }

                inProgress.Remove(type);

                if (nestedBody.Count == 0)
                {
                    nestedBody.Add(Expression.Empty());
                }

                var nestedDiff = Expression.Block(
                    [nestedSnap],
                    Expression.Assign(nestedSnap, Expression.Call(ReadDocumentMethod, p, snapType, length, arena)),
                    Expression.Block(nestedBody));

                whenFound = Expression.IfThenElse(
                    valueIsNull,
                    Expression.IfThen(Expression.Not(snapIsNull), setNull),
                    Expression.IfThenElse(
                        Expression.NotEqual(snapType, DocumentBsonType),
                        setValue,
                        nestedDiff));
            }
            else
            {
                var equals = kind is BsonTypeKind.Document or BsonTypeKind.DriverBridged
                    ? Expression.Call(ValueBytesEqualDefinition.MakeGenericMethod(type), value, p, snapType, length, arena)
                    : ElementCodecCompiler.EmitEquals(value, p, snapType, length, arena, type);

                // Same tolerance as ElementCodec: an unreadable snapshot value is "different", not a crash.
                var guardedEquals = Expression.TryCatch(
                    equals,
                    Expression.Catch(typeof(InvalidCastException), Expression.Constant(false)),
                    Expression.Catch(typeof(OverflowException), Expression.Constant(false)),
                    Expression.Catch(typeof(InvalidOperationException), Expression.Constant(false)));

                whenFound = Expression.IfThen(Expression.Not(guardedEquals), setValue);
            }

            // Not in the snapshot: a non-null value is new information; a null one is not (absent and
            // null are equivalent for our own reads and for the overwhelming majority of queries).
            Expression whenMissing = isReferenceLike
                ? Expression.IfThen(Expression.Not(valueIsNull), setValue)
                : setValue;

            return Expression.Block(
                [path, offset, p, snapType, length],
                assignPath,
                Expression.IfThenElse(
                    Expression.Call(snapshot, TryGetElementOffsetMethod, nameExpr, offset),
                    Expression.Block(loadElement, whenFound),
                    whenMissing));
        }
    }
}
