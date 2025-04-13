using MongoDB.Bson;

namespace Library.FilterUtils.ExpressionTranslators;

public class ExistsFilterElementTranslator : IFilterElementTranslator
{
    public string Operator => "$exists";

    public Expression Handle(string field, BsonValue value, ParameterExpression param)
    {
        throw new NotSupportedException(
            $"The $exists operator on field '{field}' is not supported. Use a strongly typed field model and avoid shape-based queries in application logic.");
    }
}