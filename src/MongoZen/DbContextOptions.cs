using MongoDB.Driver;
// ReSharper disable FlagArgument

namespace MongoZen;

/// <summary>
/// Provides configuration for a <see cref="DbContext"/>, including MongoDB connection details and conventions.
/// </summary>
public class DbContextOptions
{
    /// <summary>
    /// Initializes a new instance of the <see cref="DbContextOptions"/> class for a MongoDB-backed context.
    /// </summary>
    /// <param name="mongo">The MongoDB database instance.</param>
    /// <param name="conventions">Optional conventions to adjust.</param>
    public DbContextOptions(IMongoDatabase mongo, Conventions? conventions = null)
    {
        UseInMemory = false;
        Mongo = mongo;
        Conventions = conventions ?? new Conventions();
    }
    
    /// <summary>
    /// Gets or sets a value indicating whether the context should use in-memory storage.
    /// </summary>
    public bool UseInMemory { get; set; }

    /// <summary>
    /// Gets or sets conventions that influence ID mapping and other behaviors.
    /// </summary>
    public Conventions Conventions { get; set; }

    /// <summary>
    /// Gets or sets the MongoDB database used by the context.
    /// </summary>
    public IMongoDatabase? Mongo { get; set; }

    /// <summary>
    /// Initializes a new instance of the <see cref="DbContextOptions"/> class for in-memory usage.
    /// </summary>
    /// <param name="conventions">Optional conventions to adjust.</param>
    public DbContextOptions(Conventions? conventions = null)
    {
        UseInMemory = true;
        Conventions = conventions ?? new Conventions();
    }

    /// <summary>
    /// Creates a <see cref="DbContextOptions"/> configured for a real MongoDB instance.
    /// </summary>
    /// <param name="connectionString">The MongoDB connection string.</param>
    /// <param name="databaseName">The name of the database to connect to.</param>
    /// <param name="username">Optional username for authentication.</param>
    /// <param name="password">Optional password for authentication.</param>
    /// <param name="authDatabase">Optional name of the authentication database (defaults to target database if null).</param>
    /// <param name="useTls">Whether to enable TLS/SSL for the connection.</param>
    /// <param name="replicaSet">Optional replica set name to connect to.</param>
    /// <param name="conventions">Optional conventions to adjust</param>
    /// <returns>A configured <see cref="DbContextOptions"/> instance.</returns>
    /// <remarks>
    /// Example:
    /// var options = DbContextOptions.CreateForMongo(
    ///     "mongodb://localhost:27017",
    ///     "MyDatabase",
    ///     username: "admin",
    ///     password: "secret",
    ///     useTls: true,
    ///     replicaSet: "rs0"
    /// );
    /// </remarks>
    public static DbContextOptions CreateForMongo(
        string connectionString,
        string databaseName,
        string? username = null,
        string? password = null,
        string? authDatabase = null,
        bool useTls = false,
        string? replicaSet = null,
        Conventions? conventions = null)
    {
        var settings = MongoClientSettings.FromConnectionString(connectionString);
        if (!string.IsNullOrEmpty(username) && !string.IsNullOrEmpty(password))
        {
            settings.Credential = MongoCredential.CreateCredential(
                authDatabase ?? databaseName,
                username,
                password
            );
        }

        if (useTls)
        {
            settings.UseTls = true;
        }

        if (!string.IsNullOrEmpty(replicaSet))
        {
            settings.ReplicaSetName = replicaSet;
        }

        var client = new MongoClient(settings);
        var database = client.GetDatabase(databaseName);
        return new DbContextOptions(database, conventions);
    }
}
