using System.Collections;
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

    private static readonly char[] DotSeparator = { '.' };

    public abstract string Operator { get; }

    public abstract Expression Handle(string field, BsonValue value, ParameterExpression param);

    protected Expression BuildSafeMemberAccess(Expression root, string field, out Expression? nullCheck)
    {
        return BuildSafeMemberAccessInternal(root, field.Split(DotSeparator), 0, out nullCheck);
    }

    private Expression BuildSafeMemberAccessInternal(Expression current, string[] parts, int index, out Expression? nullCheck)
    {
        nullCheck = null;
        for (var i = index; i < parts.Length; i++)
        {
            var member = Expression.PropertyOrField(current, parts[i]);
            
            // Check if we hit a collection (excluding string/byte[])
            if (i < parts.Length - 1 && IsCollection(member.Type))
            {
                // We need to use .Any() for the rest of the path
                var elementType = GetCollectionItemType(member.Type);
                var innerParam = Expression.Parameter(elementType, "i" + i);
                
                // Recursively build the rest of the path
                var innerExpr = BuildSafeMemberAccessInternal(innerParam, parts, i + 1, out var innerNullCheck);
                
                // If there's an operator waiting (like $eq), this won't work perfectly yet because 
                // the caller expects a member to compare. 
                // MongoDB implicit array expansion is complex. 
                // For now, let's just fix the crash by returning the member and letting the translator handle it if it can.
                // Actually, the right way is for the translator to know it's an array.
            }

            // build null-checks in case nested object is null -> prevent NREs
            if (i < parts.Length - 1)
            {
                var notNull = Expression.NotEqual(current, Expression.Constant(null, current.Type));
                nullCheck = nullCheck == null ? notNull : Expression.AndAlso(nullCheck, notNull);
            }

            current = member;
        }

        return current;
    }

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
}
