using System.Reflection;
using MongoDB.Driver;

namespace MongoZen;

/// <summary>
/// Mockable contract for a MongoZen document store. Provides core store operations without exposing internal MongoDB/blittable details.
/// </summary>
public interface IDocumentStore : IDisposable
{
    /// <summary>
    /// Gets the conventions used by this store.
    /// </summary>
    DocumentConventions Conventions { get; }

    /// <summary>
    /// Gets the underlying MongoDB database.
    /// </summary>
    IMongoDatabase Database { get; }

    /// <summary>
    /// Opens a new session.
    /// </summary>
    IDocumentSession OpenSession(int initialArenaSize = 1024 * 1024);

    /// <summary>
    /// Scans the specified assembly for index creation tasks and executes them.
    /// </summary>
    ValueTask ExecuteIndexesAsync(Assembly assembly, CancellationToken cancellationToken = default);

    /// <summary>
    /// Scans the calling assembly for index creation tasks and executes them.
    /// </summary>
    ValueTask ExecuteIndexesAsync(CancellationToken cancellationToken = default);
}
