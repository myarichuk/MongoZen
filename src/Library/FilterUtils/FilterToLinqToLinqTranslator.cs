using System.Collections.Concurrent;
using System.Reflection;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;

// ReSharper disable MemberCanBePrivate.Global
namespace Library.FilterUtils;

public class FilterToLinqToLinqTranslator<T> : IFilterToLinqTranslator<T>, IFilterToLinqTranslator
{
    private static readonly RenderArgs<T> RenderArgs = new(BsonSerializer.LookupSerializer<T>(), BsonSerializer.SerializerRegistry);
    private static readonly ParameterExpression Param = Expression.Parameter(typeof(T), "x");

    private readonly Dictionary<string, IFilterElementTranslator> _elementTranslators;

    /// <summary>
    /// Initializes a new instance of the <see cref="FilterToLinqToLinqTranslator{T}"/> class using a custom set of filter element translators.
    /// </summary>
    /// <param name="translators">The filter element translators keyed by the operator they handle.</param>
    public FilterToLinqToLinqTranslator(IEnumerable<IFilterElementTranslator> translators) => 
        _elementTranslators = new(
            translators.ToDictionary(t => t.Operator), 
            StringComparer.InvariantCultureIgnoreCase);

    /// <summary>
    /// Initializes a new instance of the <see cref="FilterToLinqToLinqTranslator{T}"/> class using default translators from the current library.
    /// </summary>
    public FilterToLinqToLinqTranslator()
        : this(FilterElementTranslatorDiscovery.DiscoverFromLibrary()) { }

    /// <summary>
    /// Initializes a new instance of the <see cref="FilterToLinqToLinqTranslator{T}"/> class using translators discovered from the specified assemblies.
    /// </summary>
    /// <param name="assemblies">The assemblies to scan for filter element translators.</param>
    public FilterToLinqToLinqTranslator(params Assembly[] assemblies)
        : this(FilterElementTranslatorDiscovery.DiscoverFrom(assemblies)) { }

    /// <summary>
    /// Translates a MongoDB filter definition into a LINQ-compatible expression.
    /// </summary>
    /// <param name="filter">The MongoDB filter definition to translate.</param>
    /// <returns>An expression tree representing the filter.</returns>
    public Expression<Func<T, bool>> Translate(FilterDefinition<T> filter)
    {
        var doc = filter.Render(RenderArgs);
        var expr = ParseDocument(doc, Param);

        var lambda = Expression.Lambda<Func<T, bool>>(expr, Param);
        MongoLinqValidator.ValidateAndThrowIfNeeded(lambda); // ensure Mongo LINQ compatibility

        return lambda;
    }

    /// <summary>
    /// Parses a BSON document into an expression tree using the registered filter element translators.
    /// </summary>
    /// <param name="doc">The BSON document to parse.</param>
    /// <param name="param">The parameter expression representing the input type.</param>
    /// <returns>An expression tree corresponding to the parsed filter.</returns>
    private Expression ParseDocument(BsonDocument doc, ParameterExpression param)
    {
        var expressions = new List<Expression>();

        foreach (var elem in doc.Elements)
        {
            var key = elem.Name;
            var value = elem.Value;

            if (key is "$or" or "$and" or "$nor")
            {
                if (value is not BsonArray array)
                {
                    throw new NotSupportedException($"{key} must be an array");
                }

                var subExprs = new List<Expression>();
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
                    _ => throw new NotSupportedException($"Unsupported logical operator {key}"),
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
                            throw new NotSupportedException($"Operator '{op.Name}' not supported");
                        }

                        expressions.Add(translator.Handle(key, op.Value, param));
                    }
                }
                else if (value.IsBsonRegularExpression)
                {
                    // regex needs special case as it is not always bson doc
                    if (!_elementTranslators.TryGetValue("$regex", out var translator))
                    {
                        throw new NotSupportedException("Operator '$regex' not supported");
                    }

                    expressions.Add(translator.Handle(key, value, param));                    
                }
                else
                {
                    // implicit $eq
                    if (!_elementTranslators.TryGetValue("$eq", out var translator))
                    {
                        throw new NotSupportedException("Operator '$eq' not supported");
                    }

                    expressions.Add(translator.Handle(key, value, param));
                }
            }
        }

        return expressions.Aggregate(Expression.AndAlso);
    }

    public Expression Translate(BsonDocument filter, ParameterExpression parameter) => ParseDocument(filter, parameter);
}