using MongoDB.Bson;
using MongoDB.Driver;

namespace MongoZen.FilterUtils;

/// <summary>
/// Provides helpers for working with BSON documents and filters.
/// </summary>
public static class BsonExtensions
{
    /// <summary>
    /// Converts a BSON document into a MongoDB filter definition.
    /// </summary>
    /// <typeparam name="T">The document type.</typeparam>
    /// <param name="doc">The BSON document to convert.</param>
    /// <returns>The filter definition for the document type.</returns>
    public static FilterDefinition<T> ToFilterDefinition<T>(this BsonDocument doc) =>
        new BsonDocumentFilterDefinition<T>(doc);
}
