using System.Collections.Concurrent;
using System.Reflection;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;

// ReSharper disable MemberCanBePrivate.Global
namespace MongoZen.FilterUtils;

public class FilterToLinqTranslator<T> : IFilterToLinqTranslator<T>, IFilterToLinqTranslator
{
    private static readonly RenderArgs<T> RenderArgs = new(BsonSerializer.LookupSerializer<T>(), BsonSerializer.SerializerRegistry);
    private static readonly ParameterExpression Param = Expression.Parameter(typeof(T), "x");

    private readonly ConcurrentDictionary<BsonDocument, Func<T, bool>> _compiledCache = new();
    private readonly ConcurrentQueue<BsonDocument> _cacheKeys = new();
    private readonly Dictionary<string, IFilterElementTranslator> _elementTranslators;
    private readonly Conventions _conventions;
    private int _cacheCount;

    /// <summary>
    /// Initializes a new instance of the <see cref="FilterToLinqTranslator{T}"/> class using a custom set of filter element translators.
    /// </summary>
    /// <param name="translators">The filter element translators keyed by the operator they handle.</param>
    /// <param name="conventions">The conventions to use, specifically for cache sizing.</param>
    public FilterToLinqTranslator(IEnumerable<IFilterElementTranslator> translators, Conventions? conventions = null)
    {
        _elementTranslators = new(
            translators.ToDictionary(t => t.Operator),
            StringComparer.InvariantCultureIgnoreCase);
        _conventions = conventions ?? new Conventions();
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="FilterToLinqTranslator{T}"/> class using default translators from the current MongoZen.
    /// </summary>
    public FilterToLinqTranslator()
        : this(FilterElementTranslatorDiscovery.DiscoverFromMongoZen(), null)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="FilterToLinqTranslator{T}"/> class using default translators and specified conventions.
    /// </summary>
    /// <param name="conventions">The conventions to use, specifically for cache sizing.</param>
    public FilterToLinqTranslator(Conventions? conventions)
        : this(FilterElementTranslatorDiscovery.DiscoverFromMongoZen(), conventions)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="FilterToLinqTranslator{T}"/> class using translators discovered from the specified assemblies.
    /// </summary>
    /// <param name="assemblies">The assemblies to scan for filter element translators.</param>
    public FilterToLinqTranslator(params Assembly[] assemblies)
        : this(FilterElementTranslatorDiscovery.DiscoverFrom(assemblies))
    {
    }

    /// <summary>
    /// Translates a MongoDB filter definition into a LINQ-compatible expression.
    /// </summary>
    /// <param name="filter">The MongoDB filter definition to translate.</param>
    /// <returns>An expression tree representing the filter.</returns>
    public Expression<Func<T, bool>> Translate(FilterDefinition<T> filter)
    {
        var doc = filter.Render(RenderArgs);
        var expr = ParseDocument(doc, Param);

        return Expression.Lambda<Func<T, bool>>(expr, Param);
    }

    /// <summary>
    /// Gets a compiled delegate for the filter, using the cache if available.
    /// </summary>
    public Func<T, bool> GetCompiled(FilterDefinition<T> filter)
    {
        var doc = filter.Render(RenderArgs);
        
        if (_compiledCache.TryGetValue(doc, out var cached))
        {
            return cached;
        }

        var compiled = Translate(filter).Compile();
        if (_compiledCache.TryAdd(doc, compiled))
        {
            _cacheKeys.Enqueue(doc);
            if (Interlocked.Increment(ref _cacheCount) > _conventions.QueryCacheSize)
            {
                // Evict one item (the oldest)
                if (_cacheKeys.TryDequeue(out var oldKey))
                {
                    _compiledCache.TryRemove(oldKey, out _);
                    Interlocked.Decrement(ref _cacheCount);
                }
            }
        }

        return compiled;
    }

    public Expression Translate(BsonDocument filter, ParameterExpression parameter) => ParseDocument(filter, parameter);

    /// <summary>
    /// Parses a BSON document into an expression tree using the registered filter element translators.
    /// </summary>
    /// <param name="doc">The BSON document to parse.</param>
    /// <param name="param">The parameter expression representing the input type.</param>
    /// <returns>An expression tree corresponding to the parsed filter.</returns>
    private Expression ParseDocument(BsonDocument doc, ParameterExpression param)
    {
        using var expressions = new MongoZen.Collections.PooledList<Expression>(doc.ElementCount);

        foreach (var elem in doc.Elements)
        {
            var key = elem.Name;
            var value = elem.Value;

            if (key is "$or" or "$and" or "$nor")
            {
                if (value is not BsonArray array)
                {
                    throw new NotSupportedException($"Logical operator '{key}' must be an array but was {value?.BsonType}.\nOffending value: {value}");
                }

                using var subExprs = new MongoZen.Collections.PooledList<Expression>(array.Count);
                foreach (var subDoc in array.Cast<BsonDocument>())
                {
                    var subExpr = ParseDocument(subDoc, param);
                    subExprs.Add(subExpr);
                }

                var combined = key switch
                {
                    "$or" => subExprs.Aggregate(Expression.OrElse),
                    "$nor" => Expression.Not(subExprs.Aggregate(Expression.OrElse)),
                    "$and" => subExprs.Aggregate(Expression.AndAlso),
                    _ => throw new NotSupportedException($"Logical operator '{key}' is not supported.\nDocument: {doc}"),
                };

                expressions.Add(combined);
            }
            else
            {
                if (value.IsBsonDocument)
                {
                    var opDoc = value.AsBsonDocument;
                    foreach (var op in opDoc.Elements)
                    {
                        var opKey = op.Name;
                        if (!_elementTranslators.TryGetValue(opKey, out var translator))
                        {
                            throw new NotSupportedException($"Operator '{op.Name}' is not supported for field '{key}'.\nDocument: {opDoc}");
                        }

                        expressions.Add(translator.Handle(key, op.Value, param));
                    }
                }
                else if (value.IsBsonRegularExpression)
                {
                    // regex needs special case as it is not always bson doc
                    if (!_elementTranslators.TryGetValue("$regex", out var translator))
                    {
                        throw new NotSupportedException($"Operator '$regex' not supported.\nKey: {key}, Value: {value}");
                    }

                    expressions.Add(translator.Handle(key, value, param));
                }
                else
                {
                    // implicit $eq
                    if (!_elementTranslators.TryGetValue("$eq", out var translator))
                    {
                        throw new NotSupportedException(
                            $"Operator '$eq' not supported for implicit equality.\nKey: {key}, Value: {value}");
                    }

                    expressions.Add(translator.Handle(key, value, param));
                }
            }
        }

        return expressions.Count == 0 ?
            Expression.Constant(true) :
            expressions.Aggregate(Expression.AndAlso);
    }
}
