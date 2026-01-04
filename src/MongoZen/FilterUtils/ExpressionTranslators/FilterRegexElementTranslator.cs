using System.Linq.Expressions;
using System.Reflection;
using System.Text.RegularExpressions;
using MongoDB.Bson;

namespace MongoZen.FilterUtils.ExpressionTranslators;

public class FilterRegexElementTranslator: FilterElementTranslatorBase
{
    private static readonly MethodInfo? IsMatchMethod = typeof(Regex).GetMethod(nameof(Regex.IsMatch), [typeof(string), typeof(string), typeof(RegexOptions)
    ]);

    public override string Operator => "$regex";

    public override Expression Handle(string field, BsonValue value, ParameterExpression param)
    {
        string pattern;
        var options = string.Empty;

        if (value.IsBsonRegularExpression)
        {
            var regex = value.AsBsonRegularExpression;
            pattern = regex.Pattern;
            options = regex.Options;
        }
        else if (value.IsString)
        {
            pattern = value.AsString;
        }
        else if (value.IsBsonDocument)
        {
            // just in case, should never get here
            var doc = value.AsBsonDocument;
            pattern = doc["$regex"].AsString;
            if (doc.TryGetValue("$options", out var opts))
            {
                options = opts.AsString;
            }
        }
        else
        {
            throw new NotSupportedException("Invalid format for $regex operator");
        }

        var regexOptions = RegexOptions.None;
        if (options?.Contains('i') ?? false)
        {
            regexOptions |= RegexOptions.IgnoreCase;
        }

        var member = Expression.PropertyOrField(param, field);

        return Expression.Call(
            IsMatchMethod!,
            member,
            Expression.Constant(pattern),
            Expression.Constant(regexOptions));
    }
}
