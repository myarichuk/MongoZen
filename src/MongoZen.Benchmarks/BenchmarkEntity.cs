using MongoDB.Bson.Serialization.Attributes;

namespace MongoZen.Benchmarks;

public class BenchmarkEntity
{
    [BsonId]
    public string Id { get; set; } = null!;

    public string Name { get; set; } = null!;

    public int Age { get; set; }

    public string Bio { get; set; } = null!;

    public DateTime CreatedAt { get; set; }

    public List<string> Tags { get; set; } = [];

    public Dictionary<string, string> Metadata { get; set; } = [];

    public int Version { get; set; }
}
