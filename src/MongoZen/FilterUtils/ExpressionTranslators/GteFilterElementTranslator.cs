using System.Linq.Expressions;

namespace MongoZen.FilterUtils.ExpressionTranslators;

public class GteFilterElementTranslator() : BinaryOperatorFilterElementTranslator(Expression.GreaterThanOrEqual)
{
    public override string Operator => "$gte";
}
