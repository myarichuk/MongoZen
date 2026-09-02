namespace MongoZen;

/// <summary>
/// Thrown when a transaction is required for an operation but cannot be started.
/// </summary>
public sealed class TransactionRequirementException : InvalidOperationException
{
    public TransactionRequirementException(string message) : base(message) { }
}
