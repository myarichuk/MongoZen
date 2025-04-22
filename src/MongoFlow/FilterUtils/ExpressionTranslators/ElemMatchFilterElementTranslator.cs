using System.Reflection;
using MongoDB.Bson;
// ReSharper disable ComplexConditionExpression

namespace MongoFlow.FilterUtils.ExpressionTranslators;

public class ElemMatchFilterElementTranslator: FilterElementTranslatorBase
{
    public override string Operator => "$elemMatch";
    
    public override Expression Handle(string field, BsonValue value, ParameterExpression param)
    {
        var member = BuildSafeMemberAccess(param, field, out var nullCheck);

        if (!value.IsBsonDocument)
        {
            throw new ArgumentException("$elemMatch requires a BsonDocument as the value.");
        }

        var elementType = member.Type.GenericTypeArguments[0];

        var translator = FilterToLinqTranslatorFactory.Create(elementType);
        var bsonDoc = value.AsBsonDocument;

        var elementParam = Expression.Parameter(elementType, "element");
        var innerPredicate = translator.Translate(bsonDoc, elementParam);

        // lambda: element => [innerPredicate]
        var lambda = Expression.Lambda(innerPredicate, elementParam);

        // TODO: consider caching this (if proves a bottleneck)ß
        var anyMethod = typeof(Enumerable)
            .GetMethods(BindingFlags.Static | BindingFlags.Public)
            .First(m => m.Name == nameof(Enumerable.Any) && m.GetParameters().Length == 2)
            .MakeGenericMethod(elementType);

        var anyCall = Expression.Call(anyMethod, member, lambda);

        // Include null safety checks
        return nullCheck is not null
            ? Expression.AndAlso(nullCheck, anyCall)
            : anyCall;
    }
}