using MongoDB.Bson;

namespace Library.FilterUtils.ExpressionTranslators;

public class InFilterElementTranslator : FilterElementTranslatorBase
{
    public override string Operator => "$in";

    public override Expression Handle(string field, BsonValue value, ParameterExpression param)
    {
        if (!value.IsBsonArray)
        {
            throw new ArgumentException("$in requires an array of values");
        }

        var member = BuildSafeMemberAccess(param, field, out var nullCheck);
        var inExpr = BuildContainsExpression(member, value, isIn: true);
        return nullCheck != null ? Expression.AndAlso(nullCheck, inExpr) : inExpr;
    }
}