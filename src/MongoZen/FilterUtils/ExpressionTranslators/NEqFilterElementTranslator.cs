namespace MongoZen.FilterUtils.ExpressionTranslators;

// doc: https://www.mongodb.com/docs/manual/reference/operator/query/ne/
public class NEqFilterElementTranslator : BinaryOperatorFilterElementTranslator
{
    public NEqFilterElementTranslator()
        : base(Expression.NotEqual)
    {
    }

    /// <inheritdoc />
    public override string Operator => "$ne";
}
