using MongoDB.Driver;

namespace MongoZen;

internal interface IAbstractIndexCreationTask
{
    Task ExecuteAsync(IMongoDatabase database, CancellationToken cancellationToken);
}

/// <summary>
/// Base class for defining MongoDB indexes using a class-based approach.
/// </summary>
/// <typeparam name="T">The entity type.</typeparam>
public abstract class AbstractIndexCreationTask<T> : IAbstractIndexCreationTask where T : class
{
    /// <summary>
    /// Gets the name of the index. Defaults to the class name.
    /// </summary>
    public virtual string IndexName => GetType().Name;

    /// <summary>
    /// Gets the name of the collection. Defaults to the entity type name.
    /// </summary>
    public virtual string CollectionName => typeof(T).Name;

    /// <summary>
    /// Gets the MongoDB index keys builder.
    /// </summary>
    protected IndexKeysDefinitionBuilder<T> Keys => Builders<T>.IndexKeys;

    /// <summary>
    /// Creates the index model defining the keys and options.
    /// </summary>
    public abstract CreateIndexModel<T> CreateIndexModel();

    async Task IAbstractIndexCreationTask.ExecuteAsync(IMongoDatabase database, CancellationToken cancellationToken)
    {
        var collection = database.GetCollection<T>(CollectionName);
        var model = CreateIndexModel();

        // Ensure the name is set if not explicitly provided by the user in CreateIndexModel
        if (string.IsNullOrEmpty(model.Options?.Name))
        {
            var options = model.Options ?? new CreateIndexOptions();
            options.Name = IndexName;
            
            // CreateIndexModel is immutable-ish in its properties, but Options is a reference to a mutable object.
            // However, the Driver's CreateIndexModel constructor takes the options.
            // If we want to be safe, we reconstruct if Options was null.
            model = new CreateIndexModel<T>(model.Keys, options);
        }

        await collection.Indexes.CreateOneAsync(model, cancellationToken: cancellationToken).ConfigureAwait(false);
    }
}
