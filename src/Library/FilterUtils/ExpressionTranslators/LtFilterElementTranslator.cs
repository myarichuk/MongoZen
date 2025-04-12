namespace Library.FilterUtils.ExpressionTranslators;

public class LtFilterElementTranslator : BinaryOperatorFilterElementTranslator
{
    public LtFilterElementTranslator()
        : base(Expression.LessThan)
    {
    }

    public override string Operator => "$lt";
}

public class LteFilterElementTranslator : BinaryOperatorFilterElementTranslator
{
    public LteFilterElementTranslator()
        : base(Expression.LessThanOrEqual)
    {
    }

    public override string Operator => "$lte";
}