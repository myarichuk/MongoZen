using System.Linq.Expressions;

namespace MongoZen.FilterUtils.ExpressionTranslators;

// doc: https://www.mongodb.com/docs/manual/reference/operator/query/eq/
public class EqFilterElementTranslator() : BinaryOperatorFilterElementTranslator(Expression.Equal)
{
    public override string Operator => "$eq";
}
