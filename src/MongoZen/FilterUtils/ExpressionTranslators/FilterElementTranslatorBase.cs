using System.Collections;
using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Reflection;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;

namespace MongoZen.FilterUtils.ExpressionTranslators;

public abstract class FilterElementTranslatorBase : IFilterElementTranslator
{
    private static readonly MethodInfo EnumerableContainsMethod = typeof(Enumerable)
        .GetMethods(BindingFlags.Static | BindingFlags.Public)
        .First(m => m.Name == nameof(Enumerable.Contains) && m.GetParameters().Length == 2);

    private static readonly ConcurrentDictionary<Type, MethodInfo> AnyMethodCache = new();

    private static readonly char[] DotSeparator = { '.' };

    public abstract string Operator { get; }

    public abstract Expression Handle(string field, BsonValue value, ParameterExpression param);

    /// <summary>
    /// Builds an expression for a field path, supporting nested objects and MongoDB-style array expansion.
    /// </summary>
    protected Expression BuildExpression(ParameterExpression param, string field, Func<Expression, Expression> finalBuilder)
    {
        var parts = field.Split(DotSeparator);
        return BuildPathRecursive(param, parts, 0, finalBuilder);
    }

    private Expression BuildPathRecursive(Expression current, string[] parts, int index, Func<Expression, Expression> finalBuilder)
    {
        if (index == parts.Length)
        {
            return finalBuilder(current);
        }

        var member = Expression.PropertyOrField(current, parts[index]);
        
        // Null check for the CURRENT level if we are going deeper or applying final builder to a member
        // (unless it's a value type)
        Expression? nullCheck = null;
        if (!current.Type.IsValueType)
        {
            nullCheck = Expression.NotEqual(current, Expression.Constant(null, current.Type));
        }

        if (IsCollection(member.Type))
        {
            var itemType = GetCollectionItemType(member.Type);
            var innerParam = Expression.Parameter(itemType, "x" + index);
            
            // Recurse to build the rest of the path inside the .Any() lambda
            var innerBody = BuildPathRecursive(innerParam, parts, index + 1, finalBuilder);
            var lambda = Expression.Lambda(innerBody, innerParam);
            
            var anyMethod = GetAnyMethod(itemType);
            var anyCall = Expression.Call(null, anyMethod, member, lambda);
            
            return nullCheck != null ? Expression.AndAlso(nullCheck, anyCall) : anyCall;
        }

        var result = BuildPathRecursive(member, parts, index + 1, finalBuilder);
        
        return nullCheck != null ? Expression.AndAlso(nullCheck, result) : result;
    }

    protected static MethodInfo GetAnyMethod(Type elementType) =>
        AnyMethodCache.GetOrAdd(elementType, t =>
            typeof(Enumerable)
                .GetMethods(BindingFlags.Static | BindingFlags.Public)
                .First(m => m.Name == nameof(Enumerable.Any) && m.GetParameters().Length == 2)
                .MakeGenericMethod(t));

    private static bool IsCollection(Type type) =>
        type != typeof(string) && type != typeof(byte[]) && typeof(IEnumerable).IsAssignableFrom(type);

    private static Type GetCollectionItemType(Type type) =>
        type.IsArray ? type.GetElementType()! : type.GetGenericArguments().FirstOrDefault() ?? typeof(object);

    protected Expression BuildContainsExpression(Expression memberAccess, BsonValue value, bool isIn)
    {
        if (value is not BsonArray array)
        {
            throw new NotSupportedException("$in/$nin expects an array value");
        }

        var elementType = memberAccess.Type;
        
        // Optimize: Convert BSON array to a typed array instead of using Activator and manual loop
        var typedArray = Array.CreateInstance(elementType, array.Count);
        for (int i = 0; i < array.Count; i++)
        {
            var val = BsonTypeMapper.MapToDotNetValue(array[i]);
            typedArray.SetValue(val == null ? null : Convert.ChangeType(val, elementType), i);
        }

        var listExpr = Expression.Constant(typedArray);
        var containsMethod = EnumerableContainsMethod.MakeGenericMethod(elementType);

        var call = Expression.Call(null, containsMethod, listExpr, memberAccess);
        return isIn ? call : Expression.Not(call);
    }

    // Deprecated, use BuildExpression for new code
    protected Expression BuildSafeMemberAccess(Expression root, string field, out Expression? nullCheck)
    {
        nullCheck = null;
        var parts = field.Split(DotSeparator);
        var current = root;

        for (int i = 0; i < parts.Length; i++)
        {
            var member = Expression.PropertyOrField(current, parts[i]);
            if (i < parts.Length - 1 && !current.Type.IsValueType)
            {
                var notNull = Expression.NotEqual(current, Expression.Constant(null, current.Type));
                nullCheck = nullCheck == null ? notNull : Expression.AndAlso(nullCheck, notNull);
            }
            current = member;
        }

        return current;
    }
}
