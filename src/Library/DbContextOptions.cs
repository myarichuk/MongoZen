using MongoDB.Bson.Serialization.Conventions;
using MongoDB.Driver;
// ReSharper disable FlagArgument

namespace MongoFlow;

public class DbContextOptions
{
    public bool UseInMemory { get; set; }

    public Conventions Conventions { get; set; }

    public IMongoDatabase? Mongo { get; set; }

    public DbContextOptions(IMongoDatabase mongo, Conventions? conventions = null)
    {
        UseInMemory = false;
        Mongo = mongo;
        Conventions = conventions ?? new Conventions();
    }

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