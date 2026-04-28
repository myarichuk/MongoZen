using System.Diagnostics.Metrics;

namespace MongoZen.Prometheus;

/// <summary>
/// Provides high-performance metrics for MongoZen and the MongoDB driver.
/// </summary>
public static class MongoZenMetrics
{
    internal static readonly Meter Meter = new("MongoZen", "1.0.0");

    // Command Metrics
    internal static readonly Histogram<double> CommandDuration = Meter.CreateHistogram<double>(
        "mongodb_client_command_duration", "s", "Duration of MongoDB commands");
    
    internal static readonly Counter<long> CommandErrors = Meter.CreateCounter<long>(
        "mongodb_client_command_errors_total", "ea", "Total number of command errors");
    
    internal static readonly Histogram<long> CommandRequestSize = Meter.CreateHistogram<long>(
        "mongodb_client_command_request_size", "bytes", "Size of command requests");
    
    internal static readonly Histogram<long> CommandResponseSize = Meter.CreateHistogram<long>(
        "mongodb_client_command_response_size", "bytes", "Size of command responses");

    // Cursor Metrics
    internal static readonly ObservableGauge<long> OpenCursors = Meter.CreateObservableGauge<long>(
        "mongodb_client_open_cursors_count", () => GetOpenCursorsCount(), "ea", "Number of currently open cursors");
    
    internal static readonly Histogram<double> CursorDuration = Meter.CreateHistogram<double>(
        "mongodb_client_open_cursors_duration", "s", "Duration for which cursors remain open");
    
    internal static readonly Histogram<long> CursorDocumentCount = Meter.CreateHistogram<long>(
        "mongodb_client_cursor_document_count", "ea", "Number of documents fetched by a cursor");

    // Connection Metrics
    internal static readonly Counter<long> ConnectionCreationRate = Meter.CreateCounter<long>(
        "mongodb_client_connection_creation_rate", "ea", "Rate of new connection creation");
    
    internal static readonly Histogram<double> ConnectionDuration = Meter.CreateHistogram<double>(
        "mongodb_client_connection_duration", "s", "Time taken to close a connection");

    // Query Complexity
    internal static readonly Histogram<long> QueryFilterSize = Meter.CreateHistogram<long>(
        "mongodb_client_query_filter_size", "ea", "Complexity of query filters (clause/item count)");

    private static long _openCursorsCount;
    internal static void IncrementOpenCursors() => Interlocked.Increment(ref _openCursorsCount);
    internal static void DecrementOpenCursors() => Interlocked.Decrement(ref _openCursorsCount);
    private static long GetOpenCursorsCount() => Volatile.Read(ref _openCursorsCount);
}
