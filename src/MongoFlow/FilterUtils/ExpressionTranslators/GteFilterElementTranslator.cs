namespace MongoFlow.FilterUtils.ExpressionTranslators;

public class GteFilterElementTranslator : BinaryOperatorFilterElementTranslator
{
    public GteFilterElementTranslator()
        : base(Expression.GreaterThanOrEqual)
    {
    }

    public override string Operator => "$gte";
}