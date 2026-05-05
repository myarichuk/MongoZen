using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Conventions;
using MongoDB.Bson.Serialization.Serializers;

namespace MongoZen;

public class GuidSerializerConvention : ConventionBase, IMemberMapConvention
{
    private readonly GuidSerializer _serializer = new(Conventions.GuidRepresentation);

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