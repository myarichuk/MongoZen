using MongoDB.Driver;

namespace MongoFlow.FilterUtils;

public interface IFilterTranslator<T>
{
    Expression<Func<T, bool>> Translate(FilterDefinition<T> filter);
}