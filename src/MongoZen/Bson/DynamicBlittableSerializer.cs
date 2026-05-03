using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using SharpArena.Allocators;
using MongoDB.Bson;
using MongoDB.Driver;

namespace MongoZen.Bson;

/// <summary>
/// Provides high-performance, dynamic BSON serialization for any POCO using compiled Expression Trees.
/// </summary>
public static class DynamicBlittableSerializer<T>
{
    public delegate void SerializeAction(ref ArenaBsonWriter writer, T value);
    public delegate UpdateDefinition<BsonDocument>? BuildUpdateAction(T entity, BlittableBsonDocument snapshot, UpdateDefinitionBuilder<BsonDocument> builder, ArenaAllocator arena);
    
    public static readonly SerializeAction SerializeDelegate;
    public static readonly Func<BlittableBsonDocument, ArenaAllocator, T> DeserializeDelegate;
    public static readonly BuildUpdateAction BuildUpdateDelegate;

    static DynamicBlittableSerializer()
    {
        if (typeof(IBlittableDocument<T>).IsAssignableFrom(typeof(T)))
        {
            SerializeDelegate = CompileTier1Serialize();
            DeserializeDelegate = CompileTier1Deserialize();
            BuildUpdateDelegate = CompileTier1BuildUpdate();
        }
        else
        {
            SerializeDelegate = Emitter.CompileSerializer();
            DeserializeDelegate = Emitter.CompileDeserializer();
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

    private static BuildUpdateAction CompileTier1BuildUpdate()
    {
        var entityParam = Expression.Parameter(typeof(T), "entity");
        var snapshotParam = Expression.Parameter(typeof(BlittableBsonDocument), "snapshot");
        var builderParam = Expression.Parameter(typeof(UpdateDefinitionBuilder<BsonDocument>), "builder");
        var arenaParam = Expression.Parameter(typeof(ArenaAllocator), "arena");
        var method = typeof(T).GetMethod("BuildUpdate", [typeof(T), typeof(BlittableBsonDocument), typeof(UpdateDefinitionBuilder<BsonDocument>), typeof(ArenaAllocator)])!;
        return Expression.Lambda<BuildUpdateAction>(Expression.Call(null, method, entityParam, snapshotParam, builderParam, arenaParam), entityParam, snapshotParam, builderParam, arenaParam).Compile();
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
                
                body.Add(EmitPropertyWrite(writerParam, nameSpan, propValue, prop.PropertyType));
            }

            body.Add(Expression.Call(writerParam, WriteEndDocMethod));
            return Expression.Lambda<SerializeAction>(Expression.Block(body), writerParam, objParam).Compile();
        }

        private static Expression EmitPropertyWrite(ParameterExpression writer, Expression nameSpan, Expression value, Type type)
        {
            if (type.IsEnum)
            {
                var underlyingType = Enum.GetUnderlyingType(type);
                var convertedValue = Expression.Convert(value, underlyingType);
                return EmitPropertyWrite(writer, nameSpan, convertedValue, underlyingType);
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

            var body = new List<Expression> { Expression.Assign(objVar, Expression.New(type)) };

            foreach (var prop in GetValidProperties(type))
            {
                if (prop.SetMethod == null) continue;
                body.Add(EmitPropertyRead(docParam, arenaParam, objVar, prop));
            }

            body.Add(objVar);
            return Expression.Lambda<Func<BlittableBsonDocument, ArenaAllocator, T>>(Expression.Block([objVar], body), docParam, arenaParam).Compile();
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
                var underlyingType = Enum.GetUnderlyingType(type);
                var underlyingRead = type switch
                {
                    _ when underlyingType == typeof(int) => Expression.Call(doc, GetDocMethod(nameof(BlittableBsonDocument.GetInt32), typeof(int)), offsetVar),
                    _ when underlyingType == typeof(long) => Expression.Call(doc, GetDocMethod(nameof(BlittableBsonDocument.GetInt64), typeof(int)), offsetVar),
                    _ => throw new NotSupportedException($"Enum with underlying type {underlyingType} is not supported")
                };
                readExpr = Expression.Convert(underlyingRead, type);
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

            if (readExpr == null) return Expression.Empty();

            var ifFound = Expression.IfThen(
                Expression.Call(doc, typeof(BlittableBsonDocument).GetMethod(nameof(BlittableBsonDocument.TryGetElementOffset))!, nameSpan, offsetVar),
                Expression.Assign(Expression.Property(obj, prop), readExpr)
            );

            return Expression.Block([offsetVar], ifFound);
        }

        private static Expression EmitCollectionRead(ParameterExpression doc, ParameterExpression arena, ParameterExpression offset, Type type, Type elementType)
        {
            var helperType = typeof(CollectionHelper<>).MakeGenericType(elementType);
            var method = helperType.GetMethod(nameof(CollectionHelper<int>.ReadArray))!;
            
            var arrayExpr = Expression.Call(doc, GetDocMethod(nameof(BlittableBsonDocument.GetArray), typeof(int), typeof(ArenaAllocator)), offset, arena);
            var result = Expression.Call(method, arrayExpr, arena);
            
            if (type.IsArray) return result;
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
            var method = helperType.GetMethod(nameof(DictionaryHelper<>.ReadDictionary))!;

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
            var builderParam = Expression.Parameter(typeof(UpdateDefinitionBuilder<BsonDocument>), "builder");
            var arenaParam = Expression.Parameter(typeof(ArenaAllocator), "arena");

            var combinedVar = Expression.Variable(typeof(UpdateDefinition<BsonDocument>), "combined");
            var body = new List<Expression> { Expression.Assign(combinedVar, Expression.Constant(null, typeof(UpdateDefinition<BsonDocument>))) };

            foreach (var prop in GetValidProperties(type))
            {
                body.Add(EmitPropertyDiff(entityParam, snapshotParam, builderParam, arenaParam, combinedVar, prop, ""));
            }

            body.Add(combinedVar);
            return Expression.Lambda<BuildUpdateAction>(Expression.Block([combinedVar], body), entityParam, snapshotParam, builderParam, arenaParam).Compile();
        }

        private static Expression EmitPropertyDiff(Expression entity, Expression snapshot, ParameterExpression builder, ParameterExpression arena, ParameterExpression combined, PropertyInfo prop, string pathPrefix)
        {
            var type = prop.PropertyType;
            var propValue = Expression.Property(entity, prop);
            var elementName = GetElementName(prop);
            var nameSpan = Expression.Call(AsSpanMethod, Expression.Constant(elementName));
            var fullPath = string.IsNullOrEmpty(pathPrefix) ? elementName : pathPrefix + "." + elementName;
            
            var offsetVar = Expression.Variable(typeof(int), "offset");

            if (IsDocument(type))
            {
                var nestedSnap = Expression.Call(snapshot, GetDocMethod(nameof(BlittableBsonDocument.GetDocument), typeof(int), typeof(ArenaAllocator)), offsetVar, arena);
                var nestedBody = new List<Expression>();
                foreach (var nestedProp in GetValidProperties(type))
                {
                    nestedBody.Add(EmitPropertyDiff(propValue, nestedSnap, builder, arena, combined, nestedProp, fullPath));
                }

                return Expression.Block([offsetVar],
                    Expression.IfThen(
                        Expression.AndAlso(
                            Expression.NotEqual(entity, Expression.Constant(null)),
                            Expression.Call(snapshot, typeof(BlittableBsonDocument).GetMethod(nameof(BlittableBsonDocument.TryGetElementOffset))!, nameSpan, offsetVar)
                        ),
                        Expression.Block(nestedBody)
                    )
                );
            }

            if (IsCollection(type, out _) || IsDictionary(type, out _))
            {
                var setMethodColl = typeof(UpdateDefinitionBuilder<BsonDocument>)
                    .GetMethods()
                    .First(m => m is { Name: "Set", IsGenericMethod: true } && m.GetParameters().Length == 2 && m.GetParameters()[0].ParameterType.Name.StartsWith("FieldDefinition"))
                    .MakeGenericMethod(type);

                var updateExprColl = Expression.Call(builder, setMethodColl, Expression.Convert(Expression.Constant(fullPath), typeof(FieldDefinition<,>).MakeGenericType(typeof(BsonDocument), type)), propValue);
                
                var combineExprColl = Expression.Assign(combined, 
                    Expression.Condition(
                        Expression.Equal(combined, Expression.Constant(null, typeof(UpdateDefinition<BsonDocument>))),
                        updateExprColl,
                        Expression.Call(builder, typeof(UpdateDefinitionBuilder<BsonDocument>).GetMethod("Combine", [typeof(UpdateDefinition<BsonDocument>[] )])!, Expression.NewArrayInit(typeof(UpdateDefinition<BsonDocument>), combined, updateExprColl))
                    )
                );

                return Expression.IfThen(
                    Expression.NotEqual(propValue, Expression.Constant(null, type)),
                    combineExprColl
                );
            }
            
            Expression? compareExpr;
            if (type.IsEnum)
            {
                var underlyingType = Enum.GetUnderlyingType(type);
                var underlyingRead = underlyingType switch
                {
                    _ when underlyingType == typeof(int) => Expression.Call(snapshot, GetDocMethod(nameof(BlittableBsonDocument.GetInt32), typeof(int)), offsetVar),
                    _ when underlyingType == typeof(long) => Expression.Call(snapshot, GetDocMethod(nameof(BlittableBsonDocument.GetInt64), typeof(int)), offsetVar),
                    _ => throw new NotSupportedException($"Enum with underlying type {underlyingType} is not supported")
                };
                compareExpr = Expression.NotEqual(Expression.Convert(propValue, underlyingType), underlyingRead);
            }
            else
            {
                compareExpr = type switch
                {
                    _ when type == typeof(int) => Expression.NotEqual(propValue, Expression.Call(snapshot, GetDocMethod(nameof(BlittableBsonDocument.GetInt32), typeof(int)), offsetVar)),
                    _ when type == typeof(long) => Expression.NotEqual(propValue, Expression.Call(snapshot, GetDocMethod(nameof(BlittableBsonDocument.GetInt64), typeof(int)), offsetVar)),
                    _ when type == typeof(double) => Expression.NotEqual(propValue, Expression.Call(snapshot, GetDocMethod(nameof(BlittableBsonDocument.GetDouble), typeof(int)), offsetVar)),
                    _ when type == typeof(bool) => Expression.NotEqual(propValue, Expression.Call(snapshot, GetDocMethod(nameof(BlittableBsonDocument.GetBoolean), typeof(int)), offsetVar)),
                    _ when type == typeof(string) => Expression.NotEqual(propValue, Expression.Call(snapshot, GetDocMethod(nameof(BlittableBsonDocument.GetString), typeof(int)), offsetVar)),
                    _ when type == typeof(ObjectId) => Expression.NotEqual(propValue, Expression.Call(snapshot, GetDocMethod(nameof(BlittableBsonDocument.GetObjectId), typeof(int)), offsetVar)),
                    _ when type == typeof(DateTime) => Expression.NotEqual(propValue, Expression.Call(snapshot, GetDocMethod(nameof(BlittableBsonDocument.GetDateTime), typeof(int)), offsetVar)),
                    _ when type == typeof(Guid) => Expression.NotEqual(propValue, Expression.Call(snapshot, GetDocMethod(nameof(BlittableBsonDocument.GetGuid), typeof(int)), offsetVar)),
                    _ when type == typeof(decimal) => Expression.NotEqual(propValue, Expression.Call(snapshot, GetDocMethod(nameof(BlittableBsonDocument.GetDecimal128), typeof(int)), offsetVar)),
                    _ => null
                };
            }

            if (compareExpr == null) return Expression.Empty();

            var setMethod = typeof(UpdateDefinitionBuilder<BsonDocument>)
                .GetMethods()
                .First(m => m is { Name: "Set", IsGenericMethod: true } && m.GetParameters().Length == 2 && m.GetParameters()[0].ParameterType.Name.StartsWith("FieldDefinition"))
                .MakeGenericMethod(type);

            var updateExpr = Expression.Call(builder, setMethod, Expression.Convert(Expression.Constant(fullPath), typeof(FieldDefinition<,>).MakeGenericType(typeof(BsonDocument), type)), propValue);
            
            var combineExpr = Expression.Assign(combined, 
                Expression.Condition(
                    Expression.Equal(combined, Expression.Constant(null, typeof(UpdateDefinition<BsonDocument>))),
                    updateExpr,
                    Expression.Call(builder, typeof(UpdateDefinitionBuilder<BsonDocument>).GetMethod("Combine", [typeof(UpdateDefinition<BsonDocument>[] )])!, Expression.NewArrayInit(typeof(UpdateDefinition<BsonDocument>), combined, updateExpr))
                )
            );

            var ifFound = Expression.IfThen(
                Expression.Call(snapshot, typeof(BlittableBsonDocument).GetMethod(nameof(BlittableBsonDocument.TryGetElementOffset))!, nameSpan, offsetVar),
                Expression.IfThen(compareExpr, combineExpr)
            );

            return Expression.Block([offsetVar], ifFound);
        }

        private static string GetElementName(PropertyInfo prop)
        {
            var idAttr = prop.GetCustomAttribute<MongoDB.Bson.Serialization.Attributes.BsonIdAttribute>();
            if (idAttr != null || prop.Name == "Id") return "_id";

            var elementAttr = prop.GetCustomAttribute<MongoDB.Bson.Serialization.Attributes.BsonElementAttribute>();
            return elementAttr?.ElementName ?? prop.Name;
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

        private static IEnumerable<PropertyInfo> GetValidProperties(Type type)
        {
            foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!prop.CanRead) continue;
                if (prop.GetCustomAttribute<MongoDB.Bson.Serialization.Attributes.BsonIgnoreAttribute>() != null) continue;
                if (prop.PropertyType.IsByRefLike || prop.PropertyType.IsPointer) continue;
                yield return prop;
            }
        }

        private static MethodInfo GetWriterMethod(string name, params Type[] types) => typeof(ArenaBsonWriter).GetMethod(name, types)!;
        private static MethodInfo GetDocMethod(string name, params Type[] types) => typeof(BlittableBsonDocument).GetMethod(name, types)!;
    }
}
