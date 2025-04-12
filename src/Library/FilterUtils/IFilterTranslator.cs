using MongoDB.Driver;

namespace Library.FilterUtils;

public interface IFilterTranslator<T>
{
    Expression<Func<T, bool>> Translate(FilterDefinition<T> filter);
}