using MongoDB.Bson.Serialization.Attributes;

namespace MongoZen.Benchmarks;

[BsonKnownTypes(typeof(BaseData), typeof(DerivedDataA), typeof(DerivedDataB))]
public abstract class BaseData
{
    public string Type { get; set; } = null!;
}

public class DerivedDataA : BaseData
{
    public string InfoA { get; set; } = null!;
}

public class DerivedDataB : BaseData
{
    public int InfoB { get; set; }
}

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

    public BaseData? PolymorphicData { get; set; }

    public int Version { get; set; }
}
