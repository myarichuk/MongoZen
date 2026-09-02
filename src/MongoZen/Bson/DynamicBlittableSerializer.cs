using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using SharpArena.Allocators;
using MongoDB.Bson;
using MongoZen.ChangeTracking;

namespace MongoZen.Bson;

/// <summary>
/// Provides high-performance, dynamic BSON serialization for any POCO using compiled Expression Trees.
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
        }
        else
        {
            SerializeDelegate = Emitter.CompileSerializer();
            DeserializeDelegate = Emitter.CompileDeserializer();
            DeserializeIntoDelegate = Emitter.CompileDeserializerInto();
            BuildUpdateDelegate = Emitter.CompileUpdateBuilder();
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
        var method = typeof(T).GetMethod("DeserializeInto", [typeof(BlittableBsonDocument), typeof(ArenaAllocator), typeof(T)])!;
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
        private static readonly MethodInfo AsSpanMethod = typeof(MemoryExtensions).GetMethod("AsSpan", [typeof(string)])!;
        private static readonly MethodInfo WriteStartDocMethod = typeof(ArenaBsonWriter).GetMethod(nameof(ArenaBsonWriter.WriteStartDocument), Type.EmptyTypes)!;
        private static readonly MethodInfo WriteEndDocMethod = typeof(ArenaBsonWriter).GetMethod(nameof(ArenaBsonWriter.WriteEndDocument), Type.EmptyTypes)!;
        private static readonly MethodInfo WriteNameMethod = typeof(ArenaBsonWriter).GetMethod(nameof(ArenaBsonWriter.WriteName),
            [typeof(ReadOnlySpan<char>), typeof(BlittableBsonConstants.BsonType)])!;

        public static SerializeAction CompileSerializer()
        {
            var type = typeof(T);
            var writerParam = Expression.Parameter(typeof(ArenaBsonWriter).MakeByRefType(), "writer");
            var objParam = Expression.Parameter(type, "obj");

            var body = new List<Expression> { Expression.Call(writerParam, WriteStartDocMethod) };

            foreach (var prop in GetValidProperties(type))
            {
                var propValue = Expression.Property(objParam, prop);
                var elementName = GetElementName(prop);
                var nameSpan = Expression.Call(AsSpanMethod, Expression.Constant(elementName));
                
                body.Add(EmitPropertyWrite(writerParam, nameSpan, propValue, prop.PropertyType, prop));
            }

            body.Add(Expression.Call(writerParam, WriteEndDocMethod));
            return Expression.Lambda<SerializeAction>(Expression.Block(body), writerParam, objParam).Compile();
        }

        private static Expression EmitPropertyWrite(ParameterExpression writer, Expression nameSpan, Expression value, Type type, PropertyInfo? prop = null)
        {
            if (type.IsEnum)
            {
                if (IsEnumStringRepresentation(prop))
                {
                    var toStringCall = Expression.Call(value, typeof(object).GetMethod(nameof(object.ToString), Type.EmptyTypes)!);
                    var spanCall = Expression.Call(AsSpanMethod, toStringCall);
                    var writeStringMethod = GetWriterMethod(nameof(ArenaBsonWriter.WriteString), typeof(ReadOnlySpan<char>), typeof(ReadOnlySpan<char>));
                    return Expression.Call(writer, writeStringMethod, nameSpan, spanCall);
                }

                var underlyingType = Enum.GetUnderlyingType(type);
                var convertedValue = Expression.Convert(value, underlyingType);
                return EmitPropertyWrite(writer, nameSpan, convertedValue, underlyingType);
            }

            if (IsNullable(type, out var underlyingTypeNullable))
            {
                var hasValueProp = type.GetProperty("HasValue")!;
                var valueProp = type.GetProperty("Value")!;
                return Expression.IfThenElse(
                    Expression.Property(value, hasValueProp),
                    EmitPropertyWrite(writer, nameSpan, Expression.Property(value, valueProp), underlyingTypeNullable, prop),
                    Expression.Call(writer, GetWriterMethod(nameof(ArenaBsonWriter.WriteNull), typeof(ReadOnlySpan<char>)), nameSpan)
                );
            }

            var writeMethod = type switch
            {
                _ when type == typeof(int) => GetWriterMethod(nameof(ArenaBsonWriter.WriteInt32), typeof(ReadOnlySpan<char>), typeof(int)),
                _ when type == typeof(long) => GetWriterMethod(nameof(ArenaBsonWriter.WriteInt64), typeof(ReadOnlySpan<char>), typeof(long)),
                _ when type == typeof(double) => GetWriterMethod(nameof(ArenaBsonWriter.WriteDouble), typeof(ReadOnlySpan<char>), typeof(double)),
                _ when type == typeof(bool) => GetWriterMethod(nameof(ArenaBsonWriter.WriteBoolean), typeof(ReadOnlySpan<char>), typeof(bool)),
                _ when type == typeof(ObjectId) => GetWriterMethod(nameof(ArenaBsonWriter.WriteObjectId), typeof(ReadOnlySpan<char>), typeof(ObjectId)),
                _ when type == typeof(DateTime) => GetWriterMethod(nameof(ArenaBsonWriter.WriteDateTime), typeof(ReadOnlySpan<char>), typeof(DateTime)),
                _ when type == typeof(string) => GetWriterMethod(nameof(ArenaBsonWriter.WriteString), typeof(ReadOnlySpan<char>), typeof(ReadOnlySpan<char>)),
                _ when type == typeof(Guid) => GetWriterMethod(nameof(ArenaBsonWriter.WriteGuid), typeof(ReadOnlySpan<char>), typeof(Guid)),
                _ when type == typeof(decimal) => GetWriterMethod(nameof(ArenaBsonWriter.WriteDecimal128), typeof(ReadOnlySpan<char>), typeof(decimal)),
                _ => null
            };

            if (writeMethod != null)
            {
                var valExpr = type == typeof(string) ? Expression.Call(AsSpanMethod, value) : value;
                var call = Expression.Call(writer, writeMethod, nameSpan, valExpr);
                return type.IsValueType ? call : Expression.IfThen(Expression.NotEqual(value, Expression.Constant(null, type)), call);
            }

            if (IsCollection(type, out var elementType))
            {
                return EmitCollectionWrite(writer, nameSpan, value, type, elementType);
            }

            if (IsDictionary(type, out var valueType))
            {
                return EmitDictionaryWrite(writer, nameSpan, value, type, valueType);
            }

            if (type.IsClass || (type.IsValueType && !type.IsPrimitive && !type.IsEnum))
            {
                return EmitNestedWrite(writer, nameSpan, value, type);
            }

            return Expression.Empty();
        }

        private static Expression EmitCollectionWrite(ParameterExpression writer, Expression nameSpan, Expression value, Type type, Type elementType)
        {
            var helperType = typeof(CollectionHelper<>).MakeGenericType(elementType);
            var method = helperType.GetMethod(nameof(CollectionHelper<int>.WriteArray))!;
            
            return Expression.IfThen(
                Expression.NotEqual(value, Expression.Constant(null, type)),
                Expression.Call(method, writer, nameSpan, Expression.Convert(value, typeof(IEnumerable<>).MakeGenericType(elementType)))
            );
        }

        private static Expression EmitDictionaryWrite(ParameterExpression writer, Expression nameSpan, Expression value, Type type, Type valueType)
        {
            var helperType = typeof(DictionaryHelper<>).MakeGenericType(valueType);
            var method = helperType.GetMethod(nameof(DictionaryHelper<int>.WriteDictionary))!;

            return Expression.IfThen(
                Expression.NotEqual(value, Expression.Constant(null, type)),
                Expression.Call(method, writer, nameSpan, Expression.Convert(value, typeof(IDictionary<,>).MakeGenericType(typeof(string), valueType)))
            );
        }

        private static Expression EmitNestedWrite(ParameterExpression writer, Expression nameSpan, Expression value, Type type)
        {
            var serializerType = typeof(DynamicBlittableSerializer<>).MakeGenericType(type);
            var delegateField = serializerType.GetField(nameof(SerializeDelegate))!;
            var invokeCall = Expression.Invoke(Expression.Field(null, delegateField), writer, value);

            var writeBlock = Expression.Block(
                Expression.Call(writer, WriteNameMethod, nameSpan, Expression.Constant(BlittableBsonConstants.BsonType.Document)),
                invokeCall
            );

            return type.IsClass ? Expression.IfThen(Expression.NotEqual(value, Expression.Constant(null, type)), writeBlock) : writeBlock;
        }

        public static Func<BlittableBsonDocument, ArenaAllocator, T> CompileDeserializer()
        {
            var type = typeof(T);
            var docParam = Expression.Parameter(typeof(BlittableBsonDocument), "doc");
            var arenaParam = Expression.Parameter(typeof(ArenaAllocator), "arena");
            var objVar = Expression.Variable(type, "obj");

            var body = new List<Expression> { 
                Expression.Assign(objVar, Expression.New(type)),
                Expression.Invoke(Expression.Constant(CompileDeserializerInto()), docParam, arenaParam, objVar),
                objVar
            };

            return Expression.Lambda<Func<BlittableBsonDocument, ArenaAllocator, T>>(Expression.Block([objVar], body), docParam, arenaParam).Compile();
        }

        public static DeserializeIntoAction CompileDeserializerInto()
        {
            var type = typeof(T);
            var docParam = Expression.Parameter(typeof(BlittableBsonDocument), "doc");
            var arenaParam = Expression.Parameter(typeof(ArenaAllocator), "arena");
            var objParam = Expression.Parameter(type, "obj");

            var body = new List<Expression>();

            foreach (var prop in GetValidProperties(type))
            {
                // GetValidProperties admits a get-only Id (see its comment there) so it still gets
                // written; a get-only property can't be assigned to here, so it's still skipped for
                // the read/deserialize direction specifically.
                if (prop.SetMethod == null)
                {
                    continue;
                }

                body.Add(EmitPropertyRead(docParam, arenaParam, objParam, prop));
            }

            return Expression.Lambda<DeserializeIntoAction>(Expression.Block(body), docParam, arenaParam, objParam).Compile();
        }

        private static Expression EmitPropertyRead(ParameterExpression doc, ParameterExpression arena, ParameterExpression obj, PropertyInfo prop)
        {
            var elementName = GetElementName(prop);
            var nameSpan = Expression.Call(AsSpanMethod, Expression.Constant(elementName));
            var offsetVar = Expression.Variable(typeof(int), "offset");
            var type = prop.PropertyType;

            Expression? readExpr;
            if (type.IsEnum)
            {
                readExpr = BuildEnumRead(type, doc, offsetVar, prop);
            }
            else if (IsNullable(type, out var underlyingTypeNullable))
            {
                readExpr = underlyingTypeNullable switch
                {
                    _ when underlyingTypeNullable == typeof(int) => Expression.Convert(Expression.Call(doc, GetDocMethod(nameof(BlittableBsonDocument.GetInt32), typeof(int)), offsetVar), type),
                    _ when underlyingTypeNullable == typeof(long) => Expression.Convert(Expression.Call(doc, GetDocMethod(nameof(BlittableBsonDocument.GetInt64), typeof(int)), offsetVar), type),
                    _ when underlyingTypeNullable == typeof(double) => Expression.Convert(Expression.Call(doc, GetDocMethod(nameof(BlittableBsonDocument.GetDouble), typeof(int)), offsetVar), type),
                    _ when underlyingTypeNullable == typeof(bool) => Expression.Convert(Expression.Call(doc, GetDocMethod(nameof(BlittableBsonDocument.GetBoolean), typeof(int)), offsetVar), type),
                    _ when underlyingTypeNullable == typeof(ObjectId) => Expression.Convert(Expression.Call(doc, GetDocMethod(nameof(BlittableBsonDocument.GetObjectId), typeof(int)), offsetVar), type),
                    _ when underlyingTypeNullable == typeof(DateTime) => Expression.Convert(Expression.Call(doc, GetDocMethod(nameof(BlittableBsonDocument.GetDateTime), typeof(int)), offsetVar), type),
                    _ when underlyingTypeNullable == typeof(Guid) => Expression.Convert(Expression.Call(doc, GetDocMethod(nameof(BlittableBsonDocument.GetGuid), typeof(int)), offsetVar), type),
                    _ when underlyingTypeNullable == typeof(decimal) => Expression.Convert(Expression.Call(doc, GetDocMethod(nameof(BlittableBsonDocument.GetDecimal128), typeof(int)), offsetVar), type),
                    _ when underlyingTypeNullable.IsEnum => Expression.Convert(BuildEnumRead(underlyingTypeNullable, doc, offsetVar, prop), type),
                    _ => null
                };
            }
            else
            {
                readExpr = type switch
                {
                    _ when type == typeof(int) => Expression.Call(doc, GetDocMethod(nameof(BlittableBsonDocument.GetInt32), typeof(int)), offsetVar),
                    _ when type == typeof(long) => Expression.Call(doc, GetDocMethod(nameof(BlittableBsonDocument.GetInt64), typeof(int)), offsetVar),
                    _ when type == typeof(double) => Expression.Call(doc, GetDocMethod(nameof(BlittableBsonDocument.GetDouble), typeof(int)), offsetVar),
                    _ when type == typeof(bool) => Expression.Call(doc, GetDocMethod(nameof(BlittableBsonDocument.GetBoolean), typeof(int)), offsetVar),
                    _ when type == typeof(string) => Expression.Call(doc, GetDocMethod(nameof(BlittableBsonDocument.GetString), typeof(int)), offsetVar),
                    _ when type == typeof(ObjectId) => Expression.Call(doc, GetDocMethod(nameof(BlittableBsonDocument.GetObjectId), typeof(int)), offsetVar),
                    _ when type == typeof(DateTime) => Expression.Call(doc, GetDocMethod(nameof(BlittableBsonDocument.GetDateTime), typeof(int)), offsetVar),
                    _ when type == typeof(Guid) => Expression.Call(doc, GetDocMethod(nameof(BlittableBsonDocument.GetGuid), typeof(int)), offsetVar),
                    _ when type == typeof(decimal) => Expression.Call(doc, GetDocMethod(nameof(BlittableBsonDocument.GetDecimal128), typeof(int)), offsetVar),
                    _ when IsCollection(type, out var elementType) => EmitCollectionRead(doc, arena, offsetVar, type, elementType),
                    _ when IsDictionary(type, out var valueType) => EmitDictionaryRead(doc, arena, offsetVar, type, valueType),
                    _ when type.IsClass || (!type.IsPrimitive && !type.IsEnum) => EmitNestedRead(doc, arena, offsetVar, type),
                    _ => null
                };
            }

            if (readExpr == null)
            {
                return Expression.Empty();
            }

            var isNullExpr = Expression.Equal(
                Expression.Call(null, typeof(ArenaBsonReader).GetMethod(nameof(ArenaBsonReader.GetElementType))!, doc, offsetVar),
                Expression.Constant(BlittableBsonConstants.BsonType.Null)
            );

            Expression finalReadExpr = readExpr;
            if (IsNullable(type, out _) || !type.IsValueType)
            {
                finalReadExpr = Expression.Condition(isNullExpr, Expression.Constant(null, type), readExpr);
            }

            var ifFound = Expression.IfThen(
                Expression.Call(doc, typeof(BlittableBsonDocument).GetMethod(nameof(BlittableBsonDocument.TryGetElementOffset))!, nameSpan, offsetVar),
                Expression.Assign(Expression.Property(obj, prop), finalReadExpr)
            );

            return Expression.Block([offsetVar], ifFound);
        }

        private static Expression EmitCollectionRead(ParameterExpression doc, ParameterExpression arena, ParameterExpression offset, Type type, Type elementType)
        {
            var helperType = typeof(CollectionHelper<>).MakeGenericType(elementType);
            var method = helperType.GetMethod(nameof(CollectionHelper<int>.ReadArray))!;
            
            var arrayExpr = Expression.Call(doc, GetDocMethod(nameof(BlittableBsonDocument.GetArray), typeof(int), typeof(ArenaAllocator)), offset, arena);
            var result = Expression.Call(method, arrayExpr, arena);
            
            if (type.IsArray)
            {
                return result;
            }

            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>))
            {
                var listCtor = type.GetConstructor([typeof(IEnumerable<>).MakeGenericType(elementType)])!;
                return Expression.New(listCtor, result);
            }
            
            return Expression.Convert(result, type);
        }

        private static Expression EmitDictionaryRead(ParameterExpression doc, ParameterExpression arena, ParameterExpression offset, Type type, Type valueType)
        {
            var helperType = typeof(DictionaryHelper<>).MakeGenericType(valueType);
            var method = helperType.GetMethod(nameof(DictionaryHelper<object>.ReadDictionary))!;

            var nestedDocExpr = Expression.Call(doc, GetDocMethod(nameof(BlittableBsonDocument.GetDocument), typeof(int), typeof(ArenaAllocator)), offset, arena);
            var result = Expression.Call(method, nestedDocExpr, arena);

            return Expression.Convert(result, type);
        }

        private static Expression EmitNestedRead(ParameterExpression doc, ParameterExpression arena, ParameterExpression offset, Type type)
        {
            var nestedDoc = Expression.Call(doc, GetDocMethod(nameof(BlittableBsonDocument.GetDocument), typeof(int), typeof(ArenaAllocator)), offset, arena);
            var serializerType = typeof(DynamicBlittableSerializer<>).MakeGenericType(type);
            var delegateField = serializerType.GetField(nameof(DeserializeDelegate))!;
            return Expression.Invoke(Expression.Field(null, delegateField), nestedDoc, arena);
        }

        public static BuildUpdateAction CompileUpdateBuilder()
        {
            var type = typeof(T);
            var entityParam = Expression.Parameter(type, "entity");
            var snapshotParam = Expression.Parameter(typeof(BlittableBsonDocument), "snapshot");
            var builderParam = Expression.Parameter(typeof(ArenaUpdateDefinitionBuilder).MakeByRefType(), "builder");
            var arenaParam = Expression.Parameter(typeof(ArenaAllocator), "arena");
            var prefixParam = Expression.Parameter(typeof(ReadOnlySpan<char>), "pathPrefix");

            var body = new List<Expression>();

            foreach (var prop in GetValidProperties(type))
            {
                body.Add(EmitPropertyDiff(entityParam, snapshotParam, builderParam, arenaParam, prop, prefixParam));
            }

            return Expression.Lambda<BuildUpdateAction>(Expression.Block(body), entityParam, snapshotParam, builderParam, arenaParam, prefixParam).Compile();
        }

        private static Expression EmitCollectionDiff(
            Expression propValue,
            Expression snapshot,
            ParameterExpression builder,
            ParameterExpression arena,
            Type collectionType,
            Type elementType,
            ParameterExpression pathVar,
            ParameterExpression offsetVar,
            Expression elementNameExpr)
        {
            var helperType = typeof(CollectionHelper<>).MakeGenericType(elementType);
            var equalsAtOffsetMethod = helperType.GetMethod(nameof(CollectionHelper<int>.EqualsSnapshotAtOffset))!;
            var enumerableType = typeof(IEnumerable<>).MakeGenericType(elementType);
            var convertedCollection = Expression.Convert(propValue, enumerableType);
            var toBsonValueMethod = helperType.GetMethod(nameof(CollectionHelper<int>.ToBsonValue))!;
            var setBsonValueMethod = typeof(ArenaUpdateDefinitionBuilder).GetMethod("Set", [typeof(ReadOnlySpan<char>), typeof(BsonValue)])!;
            var unsetMethod = typeof(ArenaUpdateDefinitionBuilder).GetMethod("Unset", [typeof(ReadOnlySpan<char>)])!;
            var tryGetOffsetMethod = typeof(BlittableBsonDocument).GetMethod(nameof(BlittableBsonDocument.TryGetElementOffset))!;
            var getElementTypeMethod = typeof(ArenaBsonReader).GetMethod(nameof(ArenaBsonReader.GetElementType))!;
            var nullBsonType = Expression.Constant(BlittableBsonConstants.BsonType.Null);

            return Expression.Block([offsetVar],
                Expression.IfThenElse(
                    Expression.Call(snapshot, tryGetOffsetMethod, elementNameExpr, offsetVar),
                    Expression.IfThenElse(
                        Expression.NotEqual(propValue, Expression.Constant(null, collectionType)),
                        Expression.IfThen(
                            Expression.Not(Expression.Call(null, equalsAtOffsetMethod, convertedCollection, snapshot, offsetVar, arena)),
                            Expression.Call(builder, setBsonValueMethod, pathVar, Expression.Call(null, toBsonValueMethod, propValue))),
                        Expression.IfThen(
                            Expression.NotEqual(Expression.Call(null, getElementTypeMethod, snapshot, offsetVar), nullBsonType),
                            Expression.Call(builder, unsetMethod, pathVar))),
                    Expression.IfThen(
                        Expression.NotEqual(propValue, Expression.Constant(null, collectionType)),
                        Expression.Call(builder, setBsonValueMethod, pathVar, Expression.Call(null, toBsonValueMethod, propValue)))));
        }

        private static Expression EmitDictionaryDiff(
            Expression propValue,
            Expression snapshot,
            ParameterExpression builder,
            ParameterExpression arena,
            Type dictionaryType,
            Type valueType,
            ParameterExpression pathVar,
            ParameterExpression offsetVar,
            Expression elementNameExpr)
        {
            var helperType = typeof(DictionaryHelper<>).MakeGenericType(valueType);
            var equalsAtOffsetMethod = helperType.GetMethod(nameof(DictionaryHelper<int>.EqualsSnapshotAtOffset))!;
            var dictInterfaceType = typeof(IDictionary<,>).MakeGenericType(typeof(string), valueType);
            var convertedDictionary = Expression.Convert(propValue, dictInterfaceType);
            var toBsonValueMethod = helperType.GetMethod(nameof(DictionaryHelper<int>.ToBsonValue))!;
            var setBsonValueMethod = typeof(ArenaUpdateDefinitionBuilder).GetMethod("Set", [typeof(ReadOnlySpan<char>), typeof(BsonValue)])!;
            var unsetMethod = typeof(ArenaUpdateDefinitionBuilder).GetMethod("Unset", [typeof(ReadOnlySpan<char>)])!;
            var tryGetOffsetMethod = typeof(BlittableBsonDocument).GetMethod(nameof(BlittableBsonDocument.TryGetElementOffset))!;
            var getElementTypeMethod = typeof(ArenaBsonReader).GetMethod(nameof(ArenaBsonReader.GetElementType))!;
            var nullBsonType = Expression.Constant(BlittableBsonConstants.BsonType.Null);

            return Expression.Block([offsetVar],
                Expression.IfThenElse(
                    Expression.Call(snapshot, tryGetOffsetMethod, elementNameExpr, offsetVar),
                    Expression.IfThenElse(
                        Expression.NotEqual(propValue, Expression.Constant(null, dictionaryType)),
                        Expression.IfThen(
                            Expression.Not(Expression.Call(null, equalsAtOffsetMethod, convertedDictionary, snapshot, offsetVar, arena)),
                            Expression.Call(builder, setBsonValueMethod, pathVar, Expression.Call(null, toBsonValueMethod, propValue))),
                        Expression.IfThen(
                            Expression.NotEqual(Expression.Call(null, getElementTypeMethod, snapshot, offsetVar), nullBsonType),
                            Expression.Call(builder, unsetMethod, pathVar))),
                    Expression.IfThen(
                        Expression.NotEqual(propValue, Expression.Constant(null, dictionaryType)),
                        Expression.Call(builder, setBsonValueMethod, pathVar, Expression.Call(null, toBsonValueMethod, propValue)))));
        }

        private static Expression EmitPropertyDiff(Expression entity, Expression snapshot, ParameterExpression builder, ParameterExpression arena, PropertyInfo prop, Expression prefix)
        {
            var type = prop.PropertyType;
            var propValue = Expression.Property(entity, prop);
            var elementName = GetElementName(prop);
            var elementNameExpr = Expression.Call(AsSpanMethod, Expression.Constant(elementName));
            
            var pathVar = Expression.Variable(typeof(ReadOnlySpan<char>), "path");
            var pathBody = new List<Expression>();
            
            var combineMethod = typeof(ArenaUpdateDefinitionBuilder).GetMethod("CombinePath", [typeof(ReadOnlySpan<char>), typeof(string)])!;
            pathBody.Add(Expression.Assign(pathVar, Expression.Call(builder, combineMethod, prefix, Expression.Constant(elementName))));

            var offsetVar = Expression.Variable(typeof(int), "offset");

            if (IsDocument(type))
            {
                var nestedSnap = Expression.Call(snapshot, GetDocMethod(nameof(BlittableBsonDocument.GetDocument), typeof(int), typeof(ArenaAllocator)), offsetVar, arena);
                var nestedBody = new List<Expression>();
                foreach (var nestedProp in GetValidProperties(type))
                {
                    nestedBody.Add(EmitPropertyDiff(propValue, nestedSnap, builder, arena, nestedProp, pathVar));
                }

                var setMethod = typeof(ArenaUpdateDefinitionBuilder).GetMethod("SetObject")!.MakeGenericMethod(type);
                var unsetMethod = typeof(ArenaUpdateDefinitionBuilder).GetMethod("Unset", [typeof(ReadOnlySpan<char>)])!;

                pathBody.Add(
                    Expression.Block([offsetVar],
                        Expression.IfThenElse(
                            Expression.Call(snapshot, typeof(BlittableBsonDocument).GetMethod(nameof(BlittableBsonDocument.TryGetElementOffset))!, elementNameExpr, offsetVar),
                            Expression.IfThenElse(
                                Expression.Equal(propValue, Expression.Constant(null, type)),
                                Expression.Call(builder, unsetMethod, pathVar),
                                Expression.Block(nestedBody)
                            ),
                            Expression.IfThen(
                                Expression.NotEqual(propValue, Expression.Constant(null, type)),
                                Expression.Call(builder, setMethod, pathVar, propValue)
                            )
                        )
                    )
                );
            }
            else if (IsNullable(type, out var underlyingTypeNullable))
            {
                var hasValueProp = type.GetProperty("HasValue")!;
                var valueProp = type.GetProperty("Value")!;
                var setNullMethod = typeof(ArenaUpdateDefinitionBuilder).GetMethod("SetNull", [typeof(ReadOnlySpan<char>)])!;

                Expression? compareExpr = null;
                Expression? setCall = null;

                if (underlyingTypeNullable.IsEnum)
                {
                    var (enumCompare, setValueExpr, enumSetMethod) = BuildEnumDiffCompare(
                        underlyingTypeNullable, Expression.Property(propValue, valueProp), snapshot, offsetVar, prop);
                    compareExpr = enumCompare;
                    setCall = Expression.Call(builder, enumSetMethod, pathVar, setValueExpr);
                }
                else
                {
                    var readMethod = underlyingTypeNullable switch
                    {
                        _ when underlyingTypeNullable == typeof(int) => GetDocMethod(nameof(BlittableBsonDocument.GetInt32), typeof(int)),
                        _ when underlyingTypeNullable == typeof(long) => GetDocMethod(nameof(BlittableBsonDocument.GetInt64), typeof(int)),
                        _ when underlyingTypeNullable == typeof(double) => GetDocMethod(nameof(BlittableBsonDocument.GetDouble), typeof(int)),
                        _ when underlyingTypeNullable == typeof(bool) => GetDocMethod(nameof(BlittableBsonDocument.GetBoolean), typeof(int)),
                        _ when underlyingTypeNullable == typeof(ObjectId) => GetDocMethod(nameof(BlittableBsonDocument.GetObjectId), typeof(int)),
                        _ when underlyingTypeNullable == typeof(DateTime) => GetDocMethod(nameof(BlittableBsonDocument.GetDateTime), typeof(int)),
                        _ when underlyingTypeNullable == typeof(Guid) => GetDocMethod(nameof(BlittableBsonDocument.GetGuid), typeof(int)),
                        _ when underlyingTypeNullable == typeof(decimal) => GetDocMethod(nameof(BlittableBsonDocument.GetDecimal128), typeof(int)),
                        _ => null
                    };

                    if (readMethod != null)
                    {
                        var setMethod = typeof(ArenaUpdateDefinitionBuilder).GetMethod("Set", [typeof(ReadOnlySpan<char>), underlyingTypeNullable])!;
                        compareExpr = Expression.NotEqual(Expression.Property(propValue, valueProp), Expression.Call(snapshot, readMethod, offsetVar));
                        setCall = Expression.Call(builder, setMethod, pathVar, Expression.Property(propValue, valueProp));
                    }
                }

                if (compareExpr != null)
                {
                    // The typed read in compareExpr (GetInt32/GetGuid/etc., or the enum branch's own
                    // reads) throws InvalidCastException if the snapshot element is actually BSON
                    // Null (see BlittableBsonDocument's per-type accessors) - which is exactly the
                    // case on an ordinary null -> value transition. Short-circuit past the read
                    // whenever the snapshot is Null; that alone means "changed" since HasValue is
                    // known true in this branch.
                    var snapshotIsNull = Expression.Equal(
                        Expression.Call(null, typeof(ArenaBsonReader).GetMethod("GetElementType")!, snapshot, offsetVar),
                        Expression.Constant(BlittableBsonConstants.BsonType.Null));
                    compareExpr = Expression.OrElse(snapshotIsNull, compareExpr);

                    pathBody.Add(Expression.Block([offsetVar],
                        Expression.IfThenElse(
                            Expression.Call(snapshot, typeof(BlittableBsonDocument).GetMethod(nameof(BlittableBsonDocument.TryGetElementOffset))!, elementNameExpr, offsetVar),
                            Expression.IfThenElse(
                                Expression.Property(propValue, hasValueProp),
                                Expression.IfThen(compareExpr, setCall!),
                                Expression.IfThen(
                                    Expression.NotEqual(Expression.Constant(BlittableBsonConstants.BsonType.Null), Expression.Call(null, typeof(ArenaBsonReader).GetMethod("GetElementType")!, snapshot, offsetVar)),
                                    Expression.Call(builder, setNullMethod, pathVar)
                                )
                            ),
                            Expression.IfThen(
                                Expression.Property(propValue, hasValueProp),
                                setCall!
                            )
                        )
                    ));
                }
            }
            else if (IsCollection(type, out var collectionElementType))
            {
                pathBody.Add(EmitCollectionDiff(propValue, snapshot, builder, arena, type, collectionElementType, pathVar, offsetVar, elementNameExpr));
            }
            else if (IsDictionary(type, out var dictionaryValueType))
            {
                pathBody.Add(EmitDictionaryDiff(propValue, snapshot, builder, arena, type, dictionaryValueType, pathVar, offsetVar, elementNameExpr));
            }
            else if (type.IsEnum)
            {
                var (compareExpr, setValueExpr, enumSetMethod) = BuildEnumDiffCompare(type, propValue, snapshot, offsetVar, prop);
                var setCall = Expression.Call(builder, enumSetMethod, pathVar, setValueExpr);

                pathBody.Add(Expression.Block([offsetVar],
                    Expression.IfThenElse(
                        Expression.Call(snapshot, typeof(BlittableBsonDocument).GetMethod(nameof(BlittableBsonDocument.TryGetElementOffset))!, elementNameExpr, offsetVar),
                        Expression.IfThen(compareExpr, setCall),
                        setCall
                    )
                ));
            }
            else
            {
                var readMethod = type switch
                {
                    _ when type == typeof(int) => GetDocMethod(nameof(BlittableBsonDocument.GetInt32), typeof(int)),
                    _ when type == typeof(long) => GetDocMethod(nameof(BlittableBsonDocument.GetInt64), typeof(int)),
                    _ when type == typeof(double) => GetDocMethod(nameof(BlittableBsonDocument.GetDouble), typeof(int)),
                    _ when type == typeof(bool) => GetDocMethod(nameof(BlittableBsonDocument.GetBoolean), typeof(int)),
                    _ when type == typeof(string) => GetDocMethod(nameof(BlittableBsonDocument.GetString), typeof(int)),
                    _ when type == typeof(ObjectId) => GetDocMethod(nameof(BlittableBsonDocument.GetObjectId), typeof(int)),
                    _ when type == typeof(DateTime) => GetDocMethod(nameof(BlittableBsonDocument.GetDateTime), typeof(int)),
                    _ when type == typeof(Guid) => GetDocMethod(nameof(BlittableBsonDocument.GetGuid), typeof(int)),
                    _ when type == typeof(decimal) => GetDocMethod(nameof(BlittableBsonDocument.GetDecimal128), typeof(int)),
                    _ => null
                };

                if (readMethod != null)
                {
                    var compareExpr = Expression.NotEqual(propValue, Expression.Call(snapshot, readMethod, offsetVar));
                    var primitiveSetMethod = typeof(ArenaUpdateDefinitionBuilder).GetMethod("Set", [typeof(ReadOnlySpan<char>), type])!;
                    var setCall = Expression.Call(builder, primitiveSetMethod, pathVar, propValue);

                    pathBody.Add(Expression.Block([offsetVar],
                        Expression.IfThenElse(
                            Expression.Call(snapshot, typeof(BlittableBsonDocument).GetMethod(nameof(BlittableBsonDocument.TryGetElementOffset))!, elementNameExpr, offsetVar),
                            Expression.IfThen(compareExpr, setCall),
                            Expression.Call(builder, primitiveSetMethod, pathVar, propValue)
                        )
                    ));
                }
            }

            return Expression.Block([pathVar, offsetVar], pathBody);
        }

        private static bool IsNullable(Type type, out Type underlyingType)
        {
            underlyingType = Nullable.GetUnderlyingType(type)!;
            return underlyingType != null;
        }

        // A [BsonRepresentation] attribute on the property is member-scoped (resolved via the
        // driver's class map), so it must be checked directly here. Deliberately NOT falling back to
        // BsonSerializer.LookupSerializer(enumType) for attribute-less properties: that call resolves
        // AND caches a default serializer for the type in the driver's global registry as a side
        // effect, so calling it eagerly during fast-route compilation can make a later
        // BsonSerializer.RegisterSerializer(...) for that same enum type throw
        // ("already registered"), or silently diverge if registration happens after first use.
        // (Collection/dictionary elements in CollectionHelpers.cs have no PropertyInfo to check an
        // attribute against, so EnumRepresentation.IsString there still uses LookupSerializer as
        // best-effort - same caveat applies there.)
        private static bool IsEnumStringRepresentation(PropertyInfo? prop)
        {
            return prop?.GetCustomAttribute<MongoDB.Bson.Serialization.Attributes.BsonRepresentationAttribute>() is { Representation: BsonType.String };
        }

        private static string GetElementName(PropertyInfo prop)
        {
            var idAttr = prop.GetCustomAttribute<MongoDB.Bson.Serialization.Attributes.BsonIdAttribute>();
            if (idAttr != null || prop.Name == "Id")
            {
                return "_id";
            }

            if (prop.GetCustomAttribute<ConcurrencyCheckAttribute>() != null)
            {
                return "_etag";
            }

            var elementAttr = prop.GetCustomAttribute<MongoDB.Bson.Serialization.Attributes.BsonElementAttribute>();
            return elementAttr?.ElementName ?? prop.Name;
        }

        private static readonly Type[] DictionaryInterfaceDefinitions =
        [
            typeof(IDictionary<,>),
            typeof(IReadOnlyDictionary<,>)
        ];

        private static readonly Type[] CollectionInterfaceDefinitions =
        [
            typeof(IList<>),
            typeof(IReadOnlyList<>),
            typeof(ICollection<>),
            typeof(IEnumerable<>)
        ];

        // Structural (duck-typed) rather than matching a fixed list of concrete generic types, so
        // collection-shaped types from other libraries (e.g. protobuf's RepeatedField<T>/MapField<K,V>,
        // which implement IList<T>/IDictionary<K,V> but aren't List<T>/Dictionary<K,V>) are recognized
        // instead of silently falling through to nested-document serialization and losing their data.
        private static IEnumerable<Type> GetInterfacesIncludingSelf(Type type)
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

        private static bool IsDictionary(Type type, out Type valueType)
        {
            if (type != typeof(string))
            {
                foreach (var iface in GetInterfacesIncludingSelf(type))
                {
                    if (!iface.IsGenericType)
                    {
                        continue;
                    }

                    var def = iface.GetGenericTypeDefinition();
                    if (Array.IndexOf(DictionaryInterfaceDefinitions, def) >= 0)
                    {
                        var args = iface.GetGenericArguments();
                        if (args[0] == typeof(string))
                        {
                            valueType = args[1];
                            return true;
                        }

                        throw new NotSupportedException(
                            $"Type '{type}' is dictionary-shaped with key type '{args[0]}', but the fast BSON " +
                            "serializer route only supports string-keyed dictionaries (BSON documents require " +
                            "string field names). Use a string-keyed dictionary, or wrap this property so it " +
                            "goes through the official driver's serializer instead.");
                    }
                }
            }

            valueType = null!;
            return false;
        }

        private static bool IsDocument(Type type)
        {
            return type.IsClass && type != typeof(string) && !IsCollection(type, out _) && !IsDictionary(type, out _);
        }

        private static bool IsCollection(Type type, out Type elementType)
        {
            if (type == typeof(string))
            {
                elementType = null!;
                return false;
            }

            // Dictionaries take precedence: IDictionary<K,V> is itself an ICollection<KeyValuePair<K,V>>,
            // so without this guard every dictionary-shaped type would also match as a collection.
            if (IsDictionary(type, out _))
            {
                elementType = null!;
                return false;
            }

            if (type.IsArray)
            {
                elementType = type.GetElementType()!;
                return true;
            }

            foreach (var iface in GetInterfacesIncludingSelf(type))
            {
                if (!iface.IsGenericType)
                {
                    continue;
                }

                var def = iface.GetGenericTypeDefinition();
                if (Array.IndexOf(CollectionInterfaceDefinitions, def) >= 0)
                {
                    elementType = iface.GetGenericArguments()[0];
                    return true;
                }
            }

            elementType = null!;
            return false;
        }

        private static IEnumerable<PropertyInfo> GetValidProperties(Type type)
        {
            foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!prop.CanRead)
                {
                    continue;
                }

                // Get-only Id properties (constructor-assigned, e.g. immutable entities) are an
                // explicitly supported pattern elsewhere in this codebase - EntityIdAccessor's
                // BuildGetter reads them without requiring a setter, and BuildSetter tolerates the
                // missing setter as a no-op. Excluding a get-only Id here would drop "_id" from the
                // document entirely, which is worse than the extra-field problem CanWrite exists to
                // fix. Every other get-only property is still excluded to match the driver's
                // AutoMap, which requires a setter.
                var isId = prop.GetCustomAttribute<MongoDB.Bson.Serialization.Attributes.BsonIdAttribute>() != null || prop.Name == "Id";
                if (!prop.CanWrite && !isId)
                {
                    continue;
                }

                if (prop.GetIndexParameters().Length > 0)
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

        private static MethodInfo GetWriterMethod(string name, params Type[] types) => typeof(ArenaBsonWriter).GetMethod(name, types)!;
        private static MethodInfo GetDocMethod(string name, params Type[] types) => typeof(BlittableBsonDocument).GetMethod(name, types)!;

        // Shared by the scalar (type.IsEnum) and nullable (Nullable<TEnum>) read branches. Returns an
        // expression of type enumType (never Nullable<enumType> - the caller wraps with Convert for
        // the nullable case, matching how the outer null-check wrapper handles Nullable<T> uniformly).
        private static Expression BuildEnumRead(Type enumType, Expression doc, Expression offsetVar, PropertyInfo? prop)
        {
            var underlyingType = Enum.GetUnderlyingType(enumType);
            var underlyingRead = underlyingType switch
            {
                _ when underlyingType == typeof(int) => Expression.Call(doc, GetDocMethod(nameof(BlittableBsonDocument.GetInt32), typeof(int)), offsetVar),
                _ when underlyingType == typeof(long) => Expression.Call(doc, GetDocMethod(nameof(BlittableBsonDocument.GetInt64), typeof(int)), offsetVar),
                _ => throw new NotSupportedException($"Enum with underlying type {underlyingType} is not supported")
            };
            var numericRead = Expression.Convert(underlyingRead, enumType);

            if (!IsEnumStringRepresentation(prop))
            {
                return numericRead;
            }

            // Lenient like the driver's own EnumSerializer, which switches on the actual wire type
            // rather than assuming the configured representation: a document written before this
            // property was annotated (or by other, numeric-writing code) still has to be readable
            // during a gradual migration to string representation.
            var stringRead = Expression.Call(doc, GetDocMethod(nameof(BlittableBsonDocument.GetString), typeof(int)), offsetVar);
            var enumParseMethod = typeof(Enum).GetMethod(nameof(Enum.Parse), [typeof(Type), typeof(string)])!;
            var stringParsedRead = Expression.Convert(Expression.Call(enumParseMethod, Expression.Constant(enumType), stringRead), enumType);
            var elementTypeExpr = Expression.Call(null, typeof(ArenaBsonReader).GetMethod(nameof(ArenaBsonReader.GetElementType))!, doc, offsetVar);
            var isStringElement = Expression.Equal(elementTypeExpr, Expression.Constant(BlittableBsonConstants.BsonType.String));
            return Expression.Condition(isStringElement, stringParsedRead, numericRead);
        }

        // Shared by the scalar (type.IsEnum) and nullable (Nullable<TEnum>) diff branches.
        // enumValueExpr must be an expression of type enumType (the caller unwraps .Value first for
        // the nullable case - HasValue/null handling stays in the caller, mirroring how the nullable
        // numeric diff branch wraps its own readMethod-based comparison).
        private static (Expression CompareNotEqual, Expression SetValue, MethodInfo SetMethod) BuildEnumDiffCompare(
            Type enumType, Expression enumValueExpr, Expression snapshot, Expression offsetVar, PropertyInfo? prop)
        {
            if (IsEnumStringRepresentation(prop))
            {
                // Lenient like the read path: the existing snapshot may still hold a numeric value
                // from before this property used string representation, so compare against whatever's
                // actually there rather than assuming it's a string.
                var toStringCall = Expression.Call(enumValueExpr, typeof(object).GetMethod(nameof(object.ToString), Type.EmptyTypes)!);
                var stringRead = Expression.Call(snapshot, GetDocMethod(nameof(BlittableBsonDocument.GetString), typeof(int)), offsetVar);
                var stringCompare = Expression.NotEqual(toStringCall, stringRead);

                var fallbackUnderlyingType = Enum.GetUnderlyingType(enumType);
                var numericSnapshotRead = fallbackUnderlyingType == typeof(long)
                    ? Expression.Call(snapshot, GetDocMethod(nameof(BlittableBsonDocument.GetInt64), typeof(int)), offsetVar)
                    : Expression.Call(snapshot, GetDocMethod(nameof(BlittableBsonDocument.GetInt32), typeof(int)), offsetVar);
                var numericCompare = Expression.NotEqual(Expression.Convert(enumValueExpr, fallbackUnderlyingType), numericSnapshotRead);

                var elementTypeExpr = Expression.Call(null, typeof(ArenaBsonReader).GetMethod(nameof(ArenaBsonReader.GetElementType))!, snapshot, offsetVar);
                var isStringElement = Expression.Equal(elementTypeExpr, Expression.Constant(BlittableBsonConstants.BsonType.String));

                var stringSetMethod = typeof(ArenaUpdateDefinitionBuilder).GetMethod("Set", [typeof(ReadOnlySpan<char>), typeof(string)])!;
                return (Expression.Condition(isStringElement, stringCompare, numericCompare), toStringCall, stringSetMethod);
            }

            var underlyingType = Enum.GetUnderlyingType(enumType);
            var underlyingRead = underlyingType switch
            {
                _ when underlyingType == typeof(int) => Expression.Call(snapshot, GetDocMethod(nameof(BlittableBsonDocument.GetInt32), typeof(int)), offsetVar),
                _ when underlyingType == typeof(long) => Expression.Call(snapshot, GetDocMethod(nameof(BlittableBsonDocument.GetInt64), typeof(int)), offsetVar),
                _ => throw new NotSupportedException($"Enum with underlying type {underlyingType} is not supported")
            };
            var numericSetMethod = typeof(ArenaUpdateDefinitionBuilder).GetMethod("Set", [typeof(ReadOnlySpan<char>), underlyingType])!;
            var convertedValue = Expression.Convert(enumValueExpr, underlyingType);
            return (Expression.NotEqual(convertedValue, underlyingRead), convertedValue, numericSetMethod);
        }
    }
}
