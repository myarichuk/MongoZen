using System;
using System.Collections.Generic;
using MongoDB.Bson;
using MongoDB.Bson.IO;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Serializers;

namespace MongoZen;

internal class IncludeWrappingSerializer<TEntity> : SerializerBase<TEntity> where TEntity : class
{
    private readonly IBsonSerializer<TEntity> _innerSerializer;
    private readonly ISessionTracker _tracker;
    private readonly IEnumerable<(string AsField, Type ForeignType)> _includeMaps;

    public IncludeWrappingSerializer(IBsonSerializer<TEntity> innerSerializer, ISessionTracker tracker, IEnumerable<(string AsField, Type ForeignType)> includeMaps)
    {
        _innerSerializer = innerSerializer;
        _tracker = tracker;
        _includeMaps = includeMaps;
    }

    public override TEntity Deserialize(BsonDeserializationContext context, BsonDeserializationArgs args)
    {
        // To avoid full BsonDocument AST allocation for every entity, we use RawBsonDocument.
        // It provides access to elements without fully parsing the entire tree.
        var rawDoc = RawBsonDocumentSerializer.Instance.Deserialize(context);
        using (rawDoc)
        {
            foreach (var map in _includeMaps)
            {
                if (rawDoc.TryGetValue(map.AsField, out var includedArray) && includedArray.IsBsonArray)
                {
                    foreach (var includedDoc in includedArray.AsBsonArray)
                    {
                        var foreignEntity = BsonSerializer.Deserialize(includedDoc.AsBsonDocument, map.ForeignType);
                        var id = includedDoc.AsBsonDocument.GetValue("_id", BsonNull.Value);
                        if (id != BsonNull.Value)
                        {
                            var idValue = BsonTypeMapper.MapToDotNetValue(id);
                            _tracker.TrackDynamic(foreignEntity, map.ForeignType, idValue);
                        }
                    }
                }
            }

            // Now deserialize the main entity.
            // We create a SHALLOW BsonDocument that excludes our injected fields.
            // This avoids FormatException in the inner serializer while keeping memory overhead low.
            var filteredDoc = new BsonDocument();
            foreach (var element in rawDoc.Elements)
            {
                if (!element.Name.StartsWith("_included_"))
                {
                    filteredDoc.Add(element);
                }
            }

            using var docReader = new BsonDocumentReader(filteredDoc);
            var innerContext = BsonDeserializationContext.CreateRoot(docReader);
            return _innerSerializer.Deserialize(innerContext, args);
        }
    }
}
