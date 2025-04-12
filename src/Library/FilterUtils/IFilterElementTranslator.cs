using MongoDB.Bson;

namespace Library.FilterUtils;

public interface IFilterElementTranslator
{
    string Operator { get; }
    Expression Handle(string field, BsonValue value, ParameterExpression param);
}