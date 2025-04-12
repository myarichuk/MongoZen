namespace Library.FilterUtils.ExpressionTranslators;

// doc: https://www.mongodb.com/docs/manual/reference/operator/query/gt/
public class GtFilterElementTranslator : BinaryOperatorFilterElementTranslator
{
    public GtFilterElementTranslator()
        : base(Expression.GreaterThan)
    {
    }

    public override string Operator => "$gt";
}

// doc: https://www.mongodb.com/docs/manual/reference/operator/query/gte/
public class GteFilterElementTranslator : BinaryOperatorFilterElementTranslator
{
    public GteFilterElementTranslator()
        : base(Expression.GreaterThanOrEqual)
    {
    }

    public override string Operator => "$gte";
}