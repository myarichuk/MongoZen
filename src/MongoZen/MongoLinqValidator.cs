using System.Text.RegularExpressions;

namespace MongoZen;

/// <summary>
/// Validates LINQ expressions to ensure compatibility with the MongoDB LINQ provider.
/// </summary>
// TODO: review this for possible missed use cases (use Mongo driver docs!)
public static class MongoLinqValidator
{
    private static readonly Visitor ValidationVisitor = new();

    /// <summary>
    /// Validates a LINQ expression and throws when unsupported constructs are detected.
    /// </summary>
    /// <param name="expression">The expression tree to validate.</param>
    /// <exception cref="NotSupportedException">Thrown when unsupported constructs are found.</exception>
    public static void ValidateAndThrowIfNeeded(Expression expression) => 
        ValidationVisitor.Visit(expression);

    private class Visitor : ExpressionVisitor
    {
        protected override Expression VisitMethodCall(MethodCallExpression node)
        {
            // those are supported by MongoDB Linq provider
            if (node.Method.Name is not nameof(Enumerable.Contains)
                and not nameof(Enumerable.Any)
                and not nameof(Enumerable.All)
                and not nameof(Regex.IsMatch))
            {
                throw new NotSupportedException($"Method call '{node.Method.Name}' is not supported");
            }

            return base.VisitMethodCall(node);
        }

        protected override Expression VisitInvocation(InvocationExpression node) =>
            throw new NotSupportedException("Invoked expressions are not supported");

        protected override Expression VisitUnary(UnaryExpression node) =>
            node.NodeType == ExpressionType.Convert
                ? throw new NotSupportedException("Type casts are not supported")
                : base.VisitUnary(node);

        protected override Expression VisitMember(MemberExpression node) =>
            node.Expression is ConstantExpression
                ? throw new NotSupportedException("Captured constants (closures) are not supported")
                : base.VisitMember(node);
    }
}
