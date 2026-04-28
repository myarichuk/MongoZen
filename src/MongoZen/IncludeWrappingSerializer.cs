using System;
using System.Collections.Generic;
using MongoDB.Bson;
using MongoDB.Bson.IO;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Serializers;

namespace MongoZen;

internal class IncludeWrappingSerializer<TEntity>(
    IBsonSerializer<TEntity> innerSerializer,
    ISessionTracker tracker,
    IEnumerable<(string AsField, Type ForeignType)> includeMaps)
    : SerializerBase<TEntity>
    where TEntity : class
{
    public override TEntity Deserialize(BsonDeserializationContext context, BsonDeserializationArgs args)
    {
        // To avoid full BsonDocument AST allocation for every entity, we use RawBsonDocument.
        // It provides access to elements without fully parsing the entire tree.
        var rawDoc = RawBsonDocumentSerializer.Instance.Deserialize(context);
        using (rawDoc)
        {
            foreach (var map in includeMaps)
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
                            tracker.TrackDynamic(foreignEntity, map.ForeignType, DocId.From(idValue));
                        }
                    }
                }
            }

            // Now deserialize the main entity.
            // We wrap the reader to skip our injected fields.
            // This keeps memory overhead near zero as we don't copy the document.
            using var docReader = new BsonDocumentReader(rawDoc);
            using var filteringReader = new FilteringBsonReader(docReader);
            var innerContext = BsonDeserializationContext.CreateRoot(filteringReader);
            return innerSerializer.Deserialize(innerContext, args);
        }
    }

    private class FilteringBsonReader(IBsonReader inner) : WrappedBsonReader(inner)
    {
        public override BsonType ReadBsonType()
        {
            var type = base.ReadBsonType();
            while (type != BsonType.EndOfDocument && base.State == BsonReaderState.Name)
            {
                var bookmark = base.GetBookmark();
                var name = base.ReadName();
                if (name.StartsWith("_included_"))
                {
                    base.SkipValue();
                    type = base.ReadBsonType();
                }
                else
                {
                    base.ReturnToBookmark(bookmark);
                    return type;
                }
            }
            return type;
        }
    }
    private abstract class WrappedBsonReader(IBsonReader innerReader) : IBsonReader
    {
        public virtual BsonReaderState State => innerReader.State;
        public virtual void Close() => innerReader.Close();
        public virtual void Dispose() => innerReader.Dispose();
        public virtual BsonType GetCurrentBsonType() => innerReader.GetCurrentBsonType();
        public virtual BsonReaderBookmark GetBookmark() => innerReader.GetBookmark();
        public virtual void ReturnToBookmark(BsonReaderBookmark bookmark) => innerReader.ReturnToBookmark(bookmark);

        public virtual BsonBinaryData ReadBinaryData() => innerReader.ReadBinaryData();
        public virtual bool ReadBoolean() => innerReader.ReadBoolean();
        public virtual bool ReadBoolean(string name) => innerReader.ReadBoolean(name);
        public virtual BsonType ReadBsonType() => innerReader.ReadBsonType();
        public virtual long ReadDateTime() => innerReader.ReadDateTime();
        public virtual double ReadDouble() => innerReader.ReadDouble();
        public virtual double ReadDouble(string name) => innerReader.ReadDouble(name);
        public virtual void ReadEndArray() => innerReader.ReadEndArray();
        public virtual void ReadEndDocument() => innerReader.ReadEndDocument();
        public virtual int ReadInt32() => innerReader.ReadInt32();
        public virtual int ReadInt32(string name) => innerReader.ReadInt32(name);
        public virtual long ReadInt64() => innerReader.ReadInt64();
        public virtual long ReadInt64(string name) => innerReader.ReadInt64(name);
        public virtual string ReadJavaScript() => innerReader.ReadJavaScript();
        public virtual string ReadJavaScript(string name) => innerReader.ReadJavaScript(name);
        public virtual string ReadJavaScriptWithScope() => innerReader.ReadJavaScriptWithScope();
        public virtual string ReadJavaScriptWithScope(string name) => innerReader.ReadJavaScriptWithScope(name);
        public virtual void ReadMaxKey() => innerReader.ReadMaxKey();
        public virtual void ReadMaxKey(string name) => innerReader.ReadMaxKey(name);
        public virtual void ReadMinKey() => innerReader.ReadMinKey();
        public virtual void ReadMinKey(string name) => innerReader.ReadMinKey(name);
        public virtual string ReadName() => innerReader.ReadName();
        public virtual string ReadName(INameDecoder nameDecoder) => innerReader.ReadName(nameDecoder);
        public virtual void ReadNull() => innerReader.ReadNull();
        public virtual void ReadNull(string name) => innerReader.ReadNull(name);
        public virtual ObjectId ReadObjectId() => innerReader.ReadObjectId();
        public virtual void ReadStartArray() => innerReader.ReadStartArray();
        public virtual void ReadStartDocument() => innerReader.ReadStartDocument();
        public virtual string ReadString() => innerReader.ReadString();
        public virtual string ReadString(string name) => innerReader.ReadString(name);
        public virtual string ReadSymbol() => innerReader.ReadSymbol();
        public virtual string ReadSymbol(string name) => innerReader.ReadSymbol(name);
        public virtual long ReadTimestamp() => innerReader.ReadTimestamp();
        public virtual long ReadTimestamp(string name) => innerReader.ReadTimestamp(name);
        public virtual void ReadUndefined() => innerReader.ReadUndefined();
        public virtual void ReadUndefined(string name) => innerReader.ReadUndefined(name);
        public virtual void SkipName() => innerReader.SkipName();
        public virtual void SkipValue() => innerReader.SkipValue();
        public virtual Decimal128 ReadDecimal128() => innerReader.ReadDecimal128();
        public virtual Decimal128 ReadDecimal128(string name) => innerReader.ReadDecimal128(name);

        public virtual BsonType CurrentBsonType => innerReader.CurrentBsonType;
        public virtual bool IsAtEndOfFile() => innerReader.IsAtEndOfFile();
        public virtual void PopSettings() => innerReader.PopSettings();
        public virtual void PushSettings(Action<BsonReaderSettings> configurer) => innerReader.PushSettings(configurer);
        public virtual byte[] ReadBytes() => innerReader.ReadBytes();
        public virtual Guid ReadGuid() => innerReader.ReadGuid();
        public virtual Guid ReadGuid(GuidRepresentation guidRepresentation) => innerReader.ReadGuid(guidRepresentation);
        public virtual IByteBuffer ReadRawBsonArray() => innerReader.ReadRawBsonArray();
        public virtual IByteBuffer ReadRawBsonDocument() => innerReader.ReadRawBsonDocument();
        public virtual BsonRegularExpression ReadRegularExpression() => innerReader.ReadRegularExpression();
    }
}
