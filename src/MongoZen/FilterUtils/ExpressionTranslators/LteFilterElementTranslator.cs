using System.Linq.Expressions;

namespace MongoZen.FilterUtils.ExpressionTranslators;

public class LteFilterElementTranslator() : BinaryOperatorFilterElementTranslator(Expression.LessThanOrEqual)
{
    public override string Operator => "$lte";
}
