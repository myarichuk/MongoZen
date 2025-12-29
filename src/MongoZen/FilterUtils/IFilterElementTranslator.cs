using MongoDB.Bson;

namespace MongoZen.FilterUtils;

/// <summary>
/// Translates a single filter operator into a LINQ expression fragment.
/// </summary>
public interface IFilterElementTranslator
{
    /// <summary>
    /// Gets the MongoDB operator handled by this translator (e.g. <c>$eq</c>).
    /// </summary>
    string Operator { get; }

    /// <summary>
    /// Translates the operator for the given field/value pair.
    /// </summary>
    /// <param name="field">The field name in the document.</param>
    /// <param name="value">The BSON value for the operator.</param>
    /// <param name="param">The parameter expression representing the document.</param>
    /// <returns>The translated expression.</returns>
    Expression Handle(string field, BsonValue value, ParameterExpression param);
}
