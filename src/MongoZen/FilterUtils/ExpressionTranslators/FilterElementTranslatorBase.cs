using System.Linq.Expressions;
using MongoDB.Bson;

namespace MongoZen.FilterUtils.ExpressionTranslators;

public abstract class FilterElementTranslatorBase : IFilterElementTranslator
{
    public abstract string Operator { get; }

    public abstract Expression Handle(string field, BsonValue value, ParameterExpression param);

    protected Expression BuildSafeMemberAccess(Expression root, string field, out Expression? nullCheck)
    {
        var parts = field.Split('.');
        var current = root;
        Expression? check = null;

        for (var i = 0; i < parts.Length; i++)
        {
            var member = Expression.PropertyOrField(current, parts[i]);

            // build null-checks in case nested object is null -> prevent NREs
            if (i < parts.Length - 1)
            {
                var notNull = Expression.NotEqual(current, Expression.Constant(null, current.Type));
                check = check == null ? notNull : Expression.AndAlso(check, notNull);
            }

            current = member;
        }

        nullCheck = check;
        return current;
    }

    protected Expression BuildContainsExpression(Expression memberAccess, BsonValue value, bool isIn)
    {
        if (value is not BsonArray array)
        {
            throw new NotSupportedException("$in/$nin expects an array value");
        }

        var elementType = memberAccess.Type;

        var convertedValues = array
            .Select(v => Convert.ChangeType(BsonTypeMapper.MapToDotNetValue(v), elementType))
            .ToList();

        var listType = typeof(List<>).MakeGenericType(elementType);
        var typedList = (System.Collections.IList)Activator.CreateInstance(listType)! ?? throw new InvalidOperationException("Activator.CreateInstance() returned null");
        foreach (var item in convertedValues)
        {
            typedList.Add(item);
        }

        var listExpr = Expression.Constant(typedList, listType);

        // ReSharper disable once ComplexConditionExpression
        var containsMethod = typeof(Enumerable)
            .GetMethods()
            .First(m => m.Name == nameof(Enumerable.Contains) && m.GetParameters().Length == 2)
            .MakeGenericMethod(elementType);

        var call = Expression.Call(null, containsMethod, listExpr, memberAccess);
        return isIn ? call : Expression.Not(call);
    }
}
