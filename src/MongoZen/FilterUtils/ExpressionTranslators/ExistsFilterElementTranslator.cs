using MongoDB.Bson;

namespace MongoZen.FilterUtils.ExpressionTranslators;

public class ExistsFilterElementTranslator : IFilterElementTranslator
{
    /// <inheritdoc />
    public string Operator => "$exists";

    /// <inheritdoc />
    public Expression Handle(string field, BsonValue value, ParameterExpression param)
    {
        throw new NotSupportedException(
            $"The $exists operator on field '{field}' is not supported. Use a strongly typed field model and avoid shape-based queries in application logic.");
    }
}
