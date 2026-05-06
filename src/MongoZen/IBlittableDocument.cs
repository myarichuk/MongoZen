using MongoDB.Bson;
using MongoZen.Bson;
using MongoZen.ChangeTracking;
using SharpArena.Allocators;

namespace MongoZen;

/// <summary>
/// Defines a contract for documents that support high-performance, source-generated BSON operations.
/// </summary>
/// <typeparam name="T">The type of the document.</typeparam>
public interface IBlittableDocument<T>
{
    /// <summary>
    /// Performs high-performance, zero-allocation diffing between the current entity state and a BSON snapshot.
    /// </summary>
    static abstract void BuildUpdate(T entity, BlittableBsonDocument snapshot, ref ArenaUpdateDefinitionBuilder builder, ArenaAllocator arena, ReadOnlySpan<char> pathPrefix);

    /// <summary>
    /// Deserializes the document from a BlittableBsonDocument.
    /// </summary>
    static abstract T Deserialize(BlittableBsonDocument doc, ArenaAllocator arena);

    /// <summary>
    /// Serializes the document into an ArenaBsonWriter.
    /// </summary>
    static abstract void Serialize(ref ArenaBsonWriter writer, T entity);
}
