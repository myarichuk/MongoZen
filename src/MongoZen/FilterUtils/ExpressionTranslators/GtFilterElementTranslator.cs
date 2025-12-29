namespace MongoZen.FilterUtils.ExpressionTranslators;

// doc: https://www.mongodb.com/docs/manual/reference/operator/query/gt/
public class GtFilterElementTranslator : BinaryOperatorFilterElementTranslator
{
    public GtFilterElementTranslator()
        : base(Expression.GreaterThan)
    {
    }

    /// <inheritdoc />
    public override string Operator => "$gt";
}

// doc: https://www.mongodb.com/docs/manual/reference/operator/query/gte/
