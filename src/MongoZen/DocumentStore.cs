using MongoDB.Driver;

namespace MongoZen;

/// <summary>
/// Thread-safe entry point for MongoZen. Manages the connection to MongoDB and creates sessions.
/// </summary>
public sealed class DocumentStore : IDisposable
{
    private readonly IMongoClient _client;
    private readonly string _databaseName;
    private readonly IMongoDatabase _database;

    /// <summary>
    /// Initializes a new instance of the DocumentStore with a connection string and database name.
    /// </summary>
    public DocumentStore(string connectionString, string databaseName)
    {
        _client = new MongoClient(connectionString);
        _databaseName = databaseName;
        _database = _client.GetDatabase(databaseName);
    }

    /// <summary>
    /// Initializes a new instance of the DocumentStore with an existing IMongoClient.
    /// </summary>
    public DocumentStore(IMongoClient client, string databaseName)
    {
        _client = client;
        _databaseName = databaseName;
        _database = _client.GetDatabase(databaseName);
    }

    /// <summary>
    /// Gets the underlying MongoDB database.
    /// </summary>
    public IMongoDatabase Database => _database;

    /// <summary>
    /// Opens a new high-performance, unit-of-work session.
    /// </summary>
    /// <param name="initialArenaSize">The initial size of the arena allocator in bytes. Defaults to 1MB.</param>
    public DocumentSession OpenSession(int initialArenaSize = 1024 * 1024)
    {
        return new DocumentSession(_database, initialArenaSize);
    }

    public void Dispose()
    {
        // MongoClient handles its own connection pooling
    }
}
