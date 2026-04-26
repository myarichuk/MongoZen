using System;
using System.Collections.Generic;

namespace MongoZen;

/// <summary>
/// Thrown when an optimistic concurrency check fails during SaveChangesAsync.
/// </summary>
public class ConcurrencyException : Exception
{
    public ConcurrencyException(string message) : base(message)
    {
    }

    public ConcurrencyException(string message, Exception innerException) : base(message, innerException)
    {
    }

    /// <summary>
    /// The IDs of the entities that caused the concurrency conflict.
    /// </summary>
    public List<object> FailedIds { get; } = new();

    public ConcurrencyException(string message, IEnumerable<object> failedIds) : base(message)
    {
        FailedIds.AddRange(failedIds);
    }
}
