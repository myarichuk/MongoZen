using MongoDB.Bson;
using MongoDB.Driver;

namespace MongoZen.FilterUtils;

/// <summary>
/// Translates BSON-based filters into LINQ expression trees.
/// </summary>
public interface IFilterToLinqTranslator
{
    /// <summary>
    /// Translates a BSON document into a LINQ expression.
    /// </summary>
    /// <param name="filter">The BSON document representing the filter.</param>
    /// <param name="parameter">The parameter expression representing the document.</param>
    /// <returns>The translated expression.</returns>
    Expression Translate(BsonDocument filter, ParameterExpression parameter);
}

/// <summary>
/// Translates typed MongoDB filters into LINQ expression trees.
/// </summary>
/// <typeparam name="T">The document type.</typeparam>
public interface IFilterToLinqTranslator<T>
{
    /// <summary>
    /// Translates a MongoDB filter definition into a LINQ expression.
    /// </summary>
    /// <param name="filter">The filter definition to translate.</param>
    /// <returns>The translated expression.</returns>
    Expression<Func<T, bool>> Translate(FilterDefinition<T> filter);
}
