using System.Linq.Expressions;

namespace MongoZen.FilterUtils.ExpressionTranslators;

public class LtFilterElementTranslator() : BinaryOperatorFilterElementTranslator(Expression.LessThan)
{
    public override string Operator => "$lt";
}
