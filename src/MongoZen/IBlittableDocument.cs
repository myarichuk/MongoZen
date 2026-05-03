using MongoDB.Driver;
using MongoDB.Bson;
using MongoZen.Bson;

namespace MongoZen;

/// <summary>
/// Defines a contract for documents that support high-performance, source-generated BSON operations.
/// </summary>
/// <typeparam name="T">The type of the document.</typeparam>
public interface IBlittableDocument<T>
{
    /// <summary>
    /// Performs high-performance diffing between the current entity state and a BSON snapshot.
    /// </summary>
    static abstract UpdateDefinition<BsonDocument>? BuildUpdate(T entity, BlittableBsonDocument snapshot, UpdateDefinitionBuilder<BsonDocument> builder);

    /// <summary>
    /// Deserializes the document from a BlittableBsonDocument.
    /// </summary>
    static abstract T Deserialize(BlittableBsonDocument doc, SharpArena.Allocators.ArenaAllocator arena);

    /// <summary>
    /// Serializes the document into an ArenaBsonWriter.
    /// </summary>
    static abstract void Serialize(ref ArenaBsonWriter writer, T entity);
}
