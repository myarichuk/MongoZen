using System.Linq.Expressions;
using MongoDB.Bson;
// ReSharper disable ComplexConditionExpression

namespace MongoZen.FilterUtils.ExpressionTranslators;

public sealed class AllOperatorFilterElementTranslator : FilterElementTranslatorBase
{
    public override string Operator => "$all";

    public override Expression Handle(string field, BsonValue value, ParameterExpression param)
    {
        if (value is not BsonArray array)
        {
            throw new InvalidOperationException("$all requires an array of values");
        }

        var left = BuildSafeMemberAccess(param, field, out var nullCheck); // should resolve to IEnumerable<T>
        var itemType = left.Type.GetGenericArguments().First(); // e.g., string

        // Build inner loop: array.All(val => field.Contains(val))
        var containsMethod = typeof(Enumerable)
            .GetMethods()
            .First(m => m.Name == nameof(Enumerable.Contains) && m.GetParameters().Length == 2)
            .MakeGenericMethod(itemType);

        var allValues = array.Select(b => Expression.Constant(Convert.ChangeType(BsonTypeMapper.MapToDotNetValue(b), itemType)));
        var valuesArray = Expression.NewArrayInit(itemType, allValues);

        var paramVal = Expression.Parameter(itemType, "val");
        var containsCall = Expression.AndAlso(
            Expression.NotEqual(left, Expression.Constant(null)),
            Expression.Call(null, containsMethod, left, paramVal));

        var allLambda = Expression.Lambda(containsCall, paramVal);

        var allMethod = typeof(Enumerable)
            .GetMethods()
            .First(m => m.Name == nameof(Enumerable.All) && m.GetParameters().Length == 2)
            .MakeGenericMethod(itemType);

        var allExpr = Expression.Call(null, allMethod, valuesArray, allLambda);

        return nullCheck != null ? Expression.AndAlso(nullCheck, allExpr) : allExpr;
    }
}
