using MongoDB.Driver;

namespace MongoZen.Prometheus;

/// <summary>
/// Extension methods for enabling Prometheus telemetry.
/// </summary>
public static class PrometheusExtensions
{
    /// <summary>
    /// Enables high-performance Prometheus telemetry for the MongoDB client.
    /// </summary>
    public static MongoClientSettings UseMongoZenMetrics(this MongoClientSettings settings)
    {
        var subscriber = new MongoZenEventSubscriber();
        if (settings.ClusterConfigurator == null)
        {
            settings.ClusterConfigurator = cb => cb.Subscribe(subscriber);
        }
        else
        {
            var existing = settings.ClusterConfigurator;
            settings.ClusterConfigurator = cb =>
            {
                existing(cb);
                cb.Subscribe(subscriber);
            };
        }
        return settings;
    }
}
