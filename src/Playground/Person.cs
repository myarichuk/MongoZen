using MongoDB.Bson.Serialization.Attributes;

namespace Playground;

[BsonIgnoreExtraElements]
public class Person
{
    [BsonId]
    public string Id { get; set; } = null!;

    public string Name { get; set; } = null!;

    public int Age { get; set; }
}
