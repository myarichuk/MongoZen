using MongoDB.Driver;

namespace MongoZen;

internal interface IAbstractIndexCreationTask
{
    ValueTask ExecuteAsync(DocumentStore store, CancellationToken cancellationToken);
}

/// <summary>
/// Base class for defining MongoDB indexes using a class-based approach.
/// </summary>
/// <typeparam name="T">The entity type.</typeparam>
public abstract class AbstractIndexCreationTask<T> : IAbstractIndexCreationTask where T : class
{
    /// <summary>
    /// Gets the name of the index. Defaults to the class name.
    /// This is only used if <see cref="CreateIndexModel"/> does not explicitly set a name.
    /// </summary>
    public virtual string IndexName => GetType().Name;

    /// <summary>
    /// Gets the name of the collection. Defaults to the value resolved via store conventions.
    /// </summary>
    public virtual string? CollectionName => null;

    /// <summary>
    /// Gets or sets whether to drop the index if it exists with a different definition and recreate it.
    /// Use with caution in production.
    /// </summary>
    public virtual bool ForceRecreate => false;

    /// <summary>
    /// Gets the MongoDB index keys builder.
    /// </summary>
    protected IndexKeysDefinitionBuilder<T> Keys => Builders<T>.IndexKeys;

    /// <summary>
    /// Creates the index model defining the keys and options.
    /// </summary>
    public virtual CreateIndexModel<T>? CreateIndexModel() => null;

    /// <summary>
    /// Creates multiple index models. Override this if you want to define multiple indexes in a single class.
    /// </summary>
    public virtual IEnumerable<CreateIndexModel<T>> CreateIndexModels()
    {
        var single = CreateIndexModel();
        if (single != null) yield return single;
    }

    async ValueTask IAbstractIndexCreationTask.ExecuteAsync(DocumentStore store, CancellationToken cancellationToken)
    {
        var collectionName = CollectionName ?? store.Conventions.GetCollectionName(typeof(T));
        var collection = store.Database.GetCollection<T>(collectionName);
        var models = CreateIndexModels().ToList();

        if (models.Count == 0) return;

        for (int i = 0; i < models.Count; i++)
        {
            var model = models[i];
            if (string.IsNullOrEmpty(model.Options?.Name))
            {
                var options = model.Options ?? new CreateIndexOptions();
                options.Name = models.Count == 1 ? IndexName : $"{IndexName}_{i}";
                models[i] = new CreateIndexModel<T>(model.Keys, options);
            }
        }

        try
        {
            await collection.Indexes.CreateManyAsync(models, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (MongoCommandException ex) when (ex.CodeName == "IndexOptionsConflict" || ex.Message.Contains("already exists with different options"))
        {
            if (ForceRecreate)
            {
                foreach (var model in models)
                {
                    var name = model.Options?.Name ?? IndexName;
                    await collection.Indexes.DropOneAsync(name, cancellationToken).ConfigureAwait(false);
                }
                await collection.Indexes.CreateManyAsync(models, cancellationToken: cancellationToken).ConfigureAwait(false);
                return;
            }

            throw new InvalidOperationException(
                $"Index creation failed for collection '{collectionName}'. An index with the same name already exists but with a different definition. " +
                "Update the index name, manually drop the existing index, or set 'ForceRecreate = true' in your index task class.", ex);
        }
    }
}
