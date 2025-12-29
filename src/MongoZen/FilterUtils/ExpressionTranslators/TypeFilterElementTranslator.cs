using MongoDB.Bson;

namespace MongoZen.FilterUtils.ExpressionTranslators;

public class TypeFilterElementTranslator: FilterElementTranslatorBase
{
    /// <inheritdoc />
    public override string Operator => "$type";

    /// <inheritdoc />
    public override Expression Handle(string field, BsonValue value, ParameterExpression param)
    {
        var member = BuildSafeMemberAccess(param, field, out var nullCheck);
        var typeCheckExpression = value switch
        {
            { IsString: true } => GetTypeCheckExpression(member, value.AsString),
            { IsBsonArray: true } => value.AsBsonArray
                .Select(t => GetTypeCheckExpression(member, t.AsString))
                .Aggregate(Expression.OrElse),
            _ => throw new NotSupportedException("Invalid value for $type operator."),
        };

        return nullCheck is not null
            ? Expression.AndAlso(nullCheck, typeCheckExpression)
            : typeCheckExpression;
    }

    private Expression GetTypeCheckExpression(Expression member, string bsonType)
    {
        return bsonType switch
        {
            "string" => Expression.TypeIs(member, typeof(string)),
            "int" => Expression.TypeIs(member, typeof(int)),
            "long" => Expression.TypeIs(member, typeof(long)),
            "double" => Expression.TypeIs(member, typeof(double)),
            "bool" => Expression.TypeIs(member, typeof(bool)),
            "date" => Expression.TypeIs(member, typeof(DateTime)),
            "objectId" => Expression.TypeIs(member, typeof(ObjectId)),
            "guid" => Expression.TypeIs(member, typeof(Guid)),
            _ => throw new NotSupportedException($"BSON type '{bsonType}' is not supported"),
        };
    }
}
