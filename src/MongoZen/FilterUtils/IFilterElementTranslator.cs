using System.Linq.Expressions;
using MongoDB.Bson;

namespace MongoZen.FilterUtils;

public interface IFilterElementTranslator
{
    string Operator { get; }

    Expression Handle(string field, BsonValue value, ParameterExpression param);
}
