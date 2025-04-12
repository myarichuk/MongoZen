using MongoDB.Bson;

namespace Library.FilterUtils.ExpressionTranslators;

public abstract class BinaryOperatorFilterElementTranslator : FilterElementTranslatorBase
{
    private readonly Func<Expression, Expression, Expression> _expressionBuilder;

    internal BinaryOperatorFilterElementTranslator(Func<Expression, Expression, Expression> expressionBuilder) => 
        _expressionBuilder = expressionBuilder ?? throw new ArgumentNullException(nameof(expressionBuilder));

    public override Expression Handle(string field, BsonValue value, ParameterExpression param)
    {
        var dotNetValue = BsonTypeMapper.MapToDotNetValue(value); // TODO: null checks
        var left = BuildSafeMemberAccess(param, field, out var nullCheck);
        var constant = Expression.Constant(Convert.ChangeType(dotNetValue, left.Type), left.Type);

        var comparison = _expressionBuilder(left, constant);
        return nullCheck != null ? Expression.AndAlso(nullCheck, comparison) : comparison;
    }
}