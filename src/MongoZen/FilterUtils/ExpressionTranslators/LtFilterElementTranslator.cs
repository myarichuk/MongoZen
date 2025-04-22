namespace MongoZen.FilterUtils.ExpressionTranslators;

public class LtFilterElementTranslator : BinaryOperatorFilterElementTranslator
{
    public LtFilterElementTranslator()
        : base(Expression.LessThan)
    {
    }

    public override string Operator => "$lt";
}