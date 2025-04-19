using MongoDB.Bson;
using MongoDB.Driver;

namespace MongoFlow.FilterUtils;

public static class BsonExtensions
{
    public static FilterDefinition<T> ToFilterDefinition<T>(this BsonDocument doc) => 
        new BsonDocumentFilterDefinition<T>(doc);
}