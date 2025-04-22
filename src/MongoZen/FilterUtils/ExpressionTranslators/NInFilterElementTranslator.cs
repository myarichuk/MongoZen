using MongoDB.Bson;

namespace MongoZen.FilterUtils.ExpressionTranslators;

public class NInFilterElementTranslator : FilterElementTranslatorBase
{
    public override string Operator => "$nin";

    public override Expression Handle(string field, BsonValue value, ParameterExpression param)
    {
        if (!value.IsBsonArray)
        {
            throw new ArgumentException("$nin requires an array of values");
        }

        var member = BuildSafeMemberAccess(param, field, out var nullCheck);
        var inExpr = BuildContainsExpression(member, value, isIn: false);
        return nullCheck != null ? Expression.AndAlso(nullCheck, inExpr) : inExpr;
    }
}