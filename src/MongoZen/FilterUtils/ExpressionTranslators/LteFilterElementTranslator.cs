namespace MongoZen.FilterUtils.ExpressionTranslators;

public class LteFilterElementTranslator : BinaryOperatorFilterElementTranslator
{
    public LteFilterElementTranslator()
        : base(Expression.LessThanOrEqual)
    {
    }

    /// <inheritdoc />
    public override string Operator => "$lte";
}
