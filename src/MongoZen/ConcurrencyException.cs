namespace MongoZen;

/// <summary>
/// Thrown when an optimistic concurrency check fails.
/// </summary>
public sealed class ConcurrencyException : Exception
{
    /// <summary>
    /// Gets the entity that caused the concurrency exception.
    /// </summary>
    public object? Entity { get; }

    public ConcurrencyException(string message, object? entity = null, Exception? inner = null)
        : base(message, inner) => Entity = entity;
}
