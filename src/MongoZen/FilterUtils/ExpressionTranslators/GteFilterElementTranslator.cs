namespace MongoZen.FilterUtils.ExpressionTranslators;

public class GteFilterElementTranslator : BinaryOperatorFilterElementTranslator
{
    public GteFilterElementTranslator()
        : base(Expression.GreaterThanOrEqual)
    {
    }

    /// <inheritdoc />
    public override string Operator => "$gte";
}
