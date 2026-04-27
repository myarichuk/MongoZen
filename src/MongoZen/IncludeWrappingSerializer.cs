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
            // We wrap the reader to skip our injected fields.
            // This avoids FormatException in the inner serializer while keeping memory overhead zero (no extra BsonDocument).
            using var docReader = new BsonDocumentReader(rawDoc);
            using var filteringReader = new FilteringBsonReader(docReader);
            var innerContext = BsonDeserializationContext.CreateRoot(filteringReader);
            return _innerSerializer.Deserialize(innerContext, args);
        }
    }

    private class FilteringBsonReader : WrappedBsonReader
    {
        public FilteringBsonReader(IBsonReader inner) : base(inner) { }

        public override BsonType ReadBsonType()
        {
            var type = base.ReadBsonType();
            while (type != BsonType.EndOfDocument && State == BsonReaderState.Type)
            {
                var name = InnerReader.ReadName();
                if (name.StartsWith("_included_"))
                {
                    InnerReader.SkipValue();
                    type = base.ReadBsonType();
                }
                else
                {
                    // We need to return the name we just read to the caller.
                    // The standard IBsonReader doesn't have a 'unread name', 
                    // but we can use the fact that we're in 'Type' state.
                    // Actually, if we return from here, the caller will call ReadName().
                    // Since we already called ReadName(), we need to intercept that.
                    _pendingName = name;
                    return type;
                }
            }
            return type;
        }

        private string? _pendingName;

        public override string ReadName()
        {
            if (_pendingName != null)
            {
                var name = _pendingName;
                _pendingName = null;
                return name;
            }
            return base.ReadName();
        }

        public override string ReadName(INameDecoder nameDecoder)
        {
            if (_pendingName != null)
            {
                var name = _pendingName;
                _pendingName = null;
                return name;
            }
            return base.ReadName(nameDecoder);
        }
    }

    /// <summary>
    /// Base class for delegating readers to avoid boilerplate.
    /// </summary>
    private abstract class WrappedBsonReader : IBsonReader
    {
        protected readonly IBsonReader InnerReader;
        protected WrappedBsonReader(IBsonReader innerReader) => InnerReader = innerReader;

        public virtual BsonReaderState State => InnerReader.State;
        public virtual void Close() => InnerReader.Close();
        public virtual void Dispose() => InnerReader.Dispose();
        public virtual BsonType GetCurrentBsonType() => InnerReader.GetCurrentBsonType();
        public virtual string GetBookmark() => "FilteringReader:" + InnerReader.GetBookmark();
        public virtual void ReturnToBookmark(string bookmark) => InnerReader.ReturnToBookmark(bookmark.Substring(16));

        public virtual void ReadBinaryData(out byte[] bytes, out BsonBinarySubType subType) => InnerReader.ReadBinaryData(out bytes, out subType);
        public virtual void ReadBinaryData(string name, out byte[] bytes, out BsonBinarySubType subType) => InnerReader.ReadBinaryData(name, out bytes, out subType);
        public virtual bool ReadBoolean() => InnerReader.ReadBoolean();
        public virtual bool ReadBoolean(string name) => InnerReader.ReadBoolean(name);
        public virtual BsonType ReadBsonType() => InnerReader.ReadBsonType();
        public virtual void ReadDateTime(out long value) => InnerReader.ReadDateTime(out value);
        public virtual void ReadDateTime(string name, out long value) => InnerReader.ReadDateTime(name, out value);
        public virtual double ReadDouble() => InnerReader.ReadDouble();
        public virtual double ReadDouble(string name) => InnerReader.ReadDouble(name);
        public virtual void ReadEndArray() => InnerReader.ReadEndArray();
        public virtual void ReadEndDocument() => InnerReader.ReadEndDocument();
        public virtual int ReadInt32() => InnerReader.ReadInt32();
        public virtual int ReadInt32(string name) => InnerReader.ReadInt32(name);
        public virtual long ReadInt64() => InnerReader.ReadInt64();
        public virtual long ReadInt64(string name) => InnerReader.ReadInt64(name);
        public virtual string ReadJavaScript() => InnerReader.ReadJavaScript();
        public virtual string ReadJavaScript(string name) => InnerReader.ReadJavaScript(name);
        public virtual string ReadJavaScriptWithScope() => InnerReader.ReadJavaScriptWithScope();
        public virtual string ReadJavaScriptWithScope(string name) => InnerReader.ReadJavaScriptWithScope(name);
        public virtual void ReadMaxKey() => InnerReader.ReadMaxKey();
        public virtual void ReadMaxKey(string name) => InnerReader.ReadMaxKey(name);
        public virtual void ReadMinKey() => InnerReader.ReadMinKey();
        public virtual void ReadMinKey(string name) => InnerReader.ReadMinKey(name);
        public virtual string ReadName() => InnerReader.ReadName();
        public virtual string ReadName(INameDecoder nameDecoder) => InnerReader.ReadName(nameDecoder);
        public virtual void ReadNull() => InnerReader.ReadNull();
        public virtual void ReadNull(string name) => InnerReader.ReadNull(name);
        public virtual void ReadObjectId(out MongoDB.Bson.ObjectId objectId) => InnerReader.ReadObjectId(out objectId);
        public virtual void ReadObjectId(string name, out MongoDB.Bson.ObjectId objectId) => InnerReader.ReadObjectId(name, out objectId);
        public virtual string ReadRegularExpression(out string pattern, out string options) => InnerReader.ReadRegularExpression(out pattern, out options);
        public virtual string ReadRegularExpression(string name, out string pattern, out string options) => InnerReader.ReadRegularExpression(name, out pattern, out options);
        public virtual void ReadStartArray() => InnerReader.ReadStartArray();
        public virtual void ReadStartDocument() => InnerReader.ReadStartDocument();
        public virtual string ReadString() => InnerReader.ReadString();
        public virtual string ReadString(string name) => InnerReader.ReadString(name);
        public virtual string ReadSymbol() => InnerReader.ReadSymbol();
        public virtual string ReadSymbol(string name) => InnerReader.ReadSymbol(name);
        public virtual long ReadTimestamp() => InnerReader.ReadTimestamp();
        public virtual long ReadTimestamp(string name) => InnerReader.ReadTimestamp(name);
        public virtual void ReadUndefined() => InnerReader.ReadUndefined();
        public virtual void ReadUndefined(string name) => InnerReader.ReadUndefined(name);
        public virtual void SkipName() => InnerReader.SkipName();
        public virtual void SkipValue() => InnerReader.SkipValue();
        public virtual Decimal128 ReadDecimal128() => InnerReader.ReadDecimal128();
        public virtual Decimal128 ReadDecimal128(string name) => InnerReader.ReadDecimal128(name);
    }
}
