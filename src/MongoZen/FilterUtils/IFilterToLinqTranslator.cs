using System.Linq.Expressions;
using MongoDB.Bson;
using MongoDB.Driver;

namespace MongoZen.FilterUtils;

public interface IFilterToLinqTranslator
{
    Expression Translate(BsonDocument filter, ParameterExpression parameter);
}

public interface IFilterToLinqTranslator<T>
{
    Expression<Func<T, bool>> Translate(FilterDefinition<T> filter);
}
