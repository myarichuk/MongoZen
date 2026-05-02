using MongoDB.Driver;
using MongoDB.Bson;

namespace MongoZen;

public sealed class UpdateOperation<T>(object id, UpdateDefinition<BsonDocument> update, string collectionName) : IPendingUpdate
{
    public async Task ExecuteAsync(IMongoDatabase database, CancellationToken ct)
    {
        var collection = database.GetCollection<BsonDocument>(collectionName);
        var filter = Builders<BsonDocument>.Filter.Eq("_id", id);
        
        await collection.UpdateOneAsync(filter, update, cancellationToken: ct);
    }
}
