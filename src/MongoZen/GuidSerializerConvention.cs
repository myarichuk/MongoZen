using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Conventions;
using MongoDB.Bson.Serialization.Serializers;

namespace MongoZen;

public class GuidSerializerConvention : ConventionBase, IMemberMapConvention
{
    private readonly GuidSerializer _serializer;

    public GuidSerializerConvention(GuidRepresentation representation)
    {
        _serializer = new GuidSerializer(representation);
    }

    public void Apply(BsonMemberMap memberMap)
    {
        if (memberMap.MemberType == typeof(Guid))
        {
            memberMap.SetSerializer(_serializer);
        }
        else if (memberMap.MemberType == typeof(Guid?))
        {
            memberMap.SetSerializer(new NullableSerializer<Guid>(_serializer));
        }
    }
}