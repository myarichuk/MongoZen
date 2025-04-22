namespace MongoFlow.FilterUtils.ExpressionTranslators;

public class LteFilterElementTranslator : BinaryOperatorFilterElementTranslator
{
    public LteFilterElementTranslator()
        : base(Expression.LessThanOrEqual)
    {
    }

    public override string Operator => "$lte";
}