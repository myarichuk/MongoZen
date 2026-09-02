using Xunit;
using SharpArena.Allocators;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.IO;
using MongoZen.Bson;

namespace MongoZen.Tests;

public unsafe class BlittableBsonDocumentTests
{
    [Fact]
    public void Can_Create_Empty_Document()
    {
        using var arena = new ArenaAllocator();
        // Minimal empty BSON document: 5 bytes [0x05, 0x00, 0x00, 0x00, 0x00]
        byte[] raw = [0x05, 0x00, 0x00, 0x00, 0x00];
        fixed (byte* p = raw)
        {
            var doc = new BlittableBsonDocument(p, 5, default);
            Assert.Equal(5, doc.Length);
        }
    }

    [Fact]
    public void Can_Get_ReadOnlySpan_From_Document()
    {
        using var arena = new ArenaAllocator();
        byte[] raw = [0x05, 0x00, 0x00, 0x00, 0x00];
        fixed (byte* p = raw)
        {
            var doc = new BlittableBsonDocument(p, 5, default);
            var span = doc.AsReadOnlySpan();
            Assert.Equal(5, span.Length);
            Assert.Equal(0x05, span[0]);
            Assert.Equal(0x00, span[4]);
        }
    }

    [Fact]
    public void Can_Instantiate_Serializer()
    {
        using var arena = new ArenaAllocator();
        var serializer = new ArenaBsonSerializer(arena);
        Assert.NotNull(serializer);
        Assert.Equal(typeof(BlittableBsonDocument), serializer.ValueType);
    }

    [Fact]
    public void Can_Lookup_Property_Offset()
    {
        using var arena = new ArenaAllocator();
        // BSON for { "a": 1 }
        // Size: 4 (len) + 1 (type:int32) + 2 ("a\0") + 4 (val:1) + 1 (term) = 12
        byte[] raw = [12, 0, 0, 0, 0x10, (byte)'a', 0, 1, 0, 0, 0, 0];

        var doc = ArenaBsonReader.Read(raw, arena);

        Assert.True(doc.TryGetElementOffset("a", out var offset));
        Assert.Equal(4, offset); // Type is at 4
    }

    [Fact]
    public void Can_Get_Typed_Values()
    {
        using var arena = new ArenaAllocator();
        // BSON for { "i": 123, "l": 456, "d": 1.23, "b": true, "s": "hello" }
        // Let's use a simpler way to generate BSON for complex tests
        var bsonDoc = new BsonDocument
        {
            { "i", 123 },
            { "l", 456L },
            { "d", 1.23 },
            { "b", true },
            { "s", "hello" }
        };
        var raw = bsonDoc.ToBson();

        var doc = ArenaBsonReader.Read(raw, arena);

        Assert.Equal(123, doc.GetInt32("i"));
        Assert.Equal(456L, doc.GetInt64("l"));
        Assert.Equal(1.23, doc.GetDouble("d"));
        Assert.True(doc.GetBoolean("b"));
        
        var sBytes = doc.GetStringBytes("s");
        Assert.Equal("hello", System.Text.Encoding.UTF8.GetString(sBytes));
    }

    [Fact]
    public void Can_Get_Complex_Typed_Values()
    {
        using var arena = new ArenaAllocator();
        var oid = ObjectId.GenerateNewId();
        var now = DateTime.UtcNow;
        
        // note: BSON datetime is only accurate to milliseconds
        now = new DateTime(now.Year, now.Month, now.Day, now.Hour, now.Minute, now.Second, now.Millisecond, DateTimeKind.Utc);

        var bsonDoc = new BsonDocument
        {
            { "oid", oid },
            { "dt", now },
            { "nested", new BsonDocument { { "x", 1 } } }
        };
        var raw = bsonDoc.ToBson();

        var doc = ArenaBsonReader.Read(raw, arena);

        Assert.Equal(oid, doc.GetObjectId("oid"));
        Assert.Equal(now, doc.GetDateTime("dt"));

        var nested = doc.GetDocument("nested", arena);
        Assert.Equal(1, nested.GetInt32("x"));
    }

    [Fact]
    public void Accessors_Handle_Pragmatic_Coercion()
    {
        using var arena = new ArenaAllocator();
        var bsonDoc = new BsonDocument
        {
            { "i", 123 },
            { "l", 456L },
            { "d", 1.0 }
        };
        var raw = bsonDoc.ToBson();

        var doc = ArenaBsonReader.Read(raw, arena);

        // GetInt32 from Int64/Double
        Assert.Equal(456, doc.GetInt32("l"));
        Assert.Equal(1, doc.GetInt32("d"));

        // GetInt64 from Int32/Double
        Assert.Equal(123L, doc.GetInt64("i"));
        Assert.Equal(1L, doc.GetInt64("d"));

        // GetDouble from Int32/Int64
        Assert.Equal(123.0, doc.GetDouble("i"));
        Assert.Equal(456.0, doc.GetDouble("l"));
    }

    [Fact]
    public void Converter_System_Works_With_Guid()
    {
        using var arena = new ArenaAllocator();
        var guid = Guid.NewGuid();
        
        var bsonDoc = new BsonDocument
        {
            { "id", new BsonBinaryData(guid, GuidRepresentation.Standard) }
        };
        var raw = bsonDoc.ToBson();

        // Register converter
        BlittableConverter<Guid>.Register(Bson.GuidConverter.Instance);

        var doc = ArenaBsonReader.Read(raw, arena);

        Assert.Equal(guid, doc.Get<Guid>("id"));
    }

    [Fact]
    public void Can_Get_Bson_Array()
    {
        using var arena = new ArenaAllocator();
        var bsonDoc = new BsonDocument
        {
            { "arr", new BsonArray { 1, 2, 3 } }
        };
        var raw = bsonDoc.ToBson();

        var doc = ArenaBsonReader.Read(raw, arena);
        var array = doc.GetArray("arr", arena);

        int sum = 0;
        int count = 0;
        foreach (var element in array)
        {
            Assert.Equal(BlittableBsonConstants.BsonType.Int32, element.Type);
            sum += element.GetInt32();
            count++;
        }

        Assert.Equal(6, sum);
        Assert.Equal(3, count);
    }

    [Fact]
    public void Can_Get_Materialized_Bson_Array()
    {
        using var arena = new ArenaAllocator();
        var bsonDoc = new BsonDocument
        {
            { "arr", new BsonArray { "first", "second", "third" } }
        };
        var raw = bsonDoc.ToBson();

        var doc = ArenaBsonReader.Read(raw, arena);
        var array = doc.GetArray("arr", arena); // No .Materialize() needed

        Assert.Equal(3, array.Count);
        Assert.Equal("first", array[0].GetString());
        Assert.Equal("third", array[2].GetString());
        Assert.Equal("second", array[1].GetString());
    }

    [Fact]
    public void Serializer_Roundtrip_Works()
    {
        using var arena = new ArenaAllocator();
        var serializer = new ArenaBsonSerializer(arena);

        // BSON for { "a": 1 }
        byte[] raw = [12, 0, 0, 0, 0x10, (byte)'a', 0, 1, 0, 0, 0, 0];
        
        // Deserialize
        using var msIn = new MemoryStream(raw);
        using var reader = new BsonBinaryReader(msIn);
        var context = BsonDeserializationContext.CreateRoot(reader);
        var doc = serializer.Deserialize(context, default);

        Assert.Equal(12, doc.Length);
        Assert.True(doc.TryGetElementOffset("a", out var offset));
        Assert.Equal(4, offset);

        // Serialize
        using var msOut = new MemoryStream();
        using var writer = new BsonBinaryWriter(msOut);
        var serializeContext = BsonSerializationContext.CreateRoot(writer);
        serializer.Serialize(serializeContext, default, doc);
        writer.Flush();

        var result = msOut.ToArray();
        Assert.Equal(raw, result);
    }
}
