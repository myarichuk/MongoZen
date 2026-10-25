using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoZen.Bson;
using MongoZen.ChangeTracking;
using SharpArena.Allocators;
using Xunit;

namespace MongoZen.Tests;

/// <summary>
/// Pins the reflection-tier type model: every supported shape round-trips through our writer and the
/// official driver's parser, nothing is silently dropped, and an unsupported member fails loudly with
/// the offending path in the message. These are the cases the audit found being dropped
/// (float, short), mangled (TimeSpan as a document of its properties, List&lt;Guid&gt; as empty
/// documents) or thrown from the driver's root-level writer (byte[], List&lt;enum&gt;, Dictionary&lt;string, double&gt;).
/// </summary>
public class SerializerTypeCoverageTests
{
    public enum Color { Red = 1, Green = 2 }
    public enum LongEnum : long { A = 1L << 40 }

    public class Address { public string? City { get; set; } public int Zip { get; set; } }

    public class EveryScalar
    {
        public int Id { get; set; }
        public short Short { get; set; }
        public ushort UShort { get; set; }
        public byte Byte { get; set; }
        public sbyte SByte { get; set; }
        public char Char { get; set; }
        public uint UInt { get; set; }
        public ulong ULong { get; set; }
        public float Float { get; set; }
        public double Double { get; set; }
        public long Long { get; set; }
        public bool Bool { get; set; }
        public string? Text { get; set; }
        public DateTime When { get; set; }
        public Guid Guid { get; set; }
        public decimal Money { get; set; }
        public ObjectId Oid { get; set; }
        public Color Color { get; set; }
        public LongEnum Big { get; set; }
        public float? NullableFloat { get; set; }
        public Color? NullableColor { get; set; }
        public byte[]? Blob { get; set; }
    }

    public class Collections
    {
        public int Id { get; set; }
        public List<Guid>? Guids { get; set; }
        public List<Color>? Colors { get; set; }
        public List<decimal>? Prices { get; set; }
        public List<float>? Floats { get; set; }
        public List<List<int>>? Matrix { get; set; }
        public List<Address>? Addresses { get; set; }
        public HashSet<string>? Tags { get; set; }
        public IReadOnlyList<int>? ReadOnly { get; set; }
        public int[]? Array { get; set; }
        public List<byte[]>? Blobs { get; set; }
        public Dictionary<string, double>? Scores { get; set; }
        public Dictionary<string, Guid>? Ids { get; set; }
        public Dictionary<string, List<int>>? Lists { get; set; }
        public Dictionary<string, Address>? Places { get; set; }
        public Dictionary<string, bool>? Flags { get; set; }
        public IReadOnlyDictionary<string, long>? ReadOnlyCounts { get; set; }
    }

    private static (T roundTripped, BsonDocument asSeenByDriver) RoundTrip<T>(T poco)
    {
        using var arena = new ArenaAllocator(1 << 20);
        var writer = new ArenaBsonWriter(arena);
        DynamicBlittableSerializer<T>.SerializeDelegate(ref writer, poco);
        var doc = writer.Commit(arena);
        var bytes = doc.AsReadOnlySpan().ToArray();

        var parsed = BsonSerializer.Deserialize<BsonDocument>(bytes); // the official driver must accept our bytes
        var back = DynamicBlittableSerializer<T>.DeserializeDelegate(ArenaBsonReader.Read(bytes, arena), arena);
        return (back, parsed);
    }

    [Fact]
    public void Every_Scalar_Round_Trips_And_Is_Valid_Bson()
    {
        var poco = new EveryScalar
        {
            Id = 1, Short = -7, UShort = 65000, Byte = 200, SByte = -100, Char = 'Ω', UInt = 4_000_000_000u, ULong = ulong.MaxValue - 5,
            Float = 1.5f, Double = 2.25, Long = 1L << 40, Bool = true, Text = "héllo", When = new DateTime(2024, 5, 6, 7, 8, 9, 123, DateTimeKind.Utc),
            Guid = Guid.NewGuid(), Money = 12345.6789m, Oid = ObjectId.GenerateNewId(), Color = Color.Green, Big = LongEnum.A,
            NullableFloat = 3.5f, NullableColor = Color.Red, Blob = [1, 2, 3, 250],
        };

        var (back, driver) = RoundTrip(poco);

        Assert.Equal(poco.Short, back.Short);
        Assert.Equal(poco.UShort, back.UShort);
        Assert.Equal(poco.Byte, back.Byte);
        Assert.Equal(poco.SByte, back.SByte);
        Assert.Equal(poco.Char, back.Char);
        Assert.Equal(poco.UInt, back.UInt);
        Assert.Equal(poco.ULong, back.ULong);
        Assert.Equal(poco.Float, back.Float);
        Assert.Equal(poco.Double, back.Double);
        Assert.Equal(poco.Long, back.Long);
        Assert.True(back.Bool);
        Assert.Equal(poco.Text, back.Text);
        Assert.Equal(poco.When, back.When);
        Assert.Equal(poco.Guid, back.Guid);
        Assert.Equal(poco.Money, back.Money);
        Assert.Equal(poco.Oid, back.Oid);
        Assert.Equal(poco.Color, back.Color);
        Assert.Equal(poco.Big, back.Big);
        Assert.Equal(poco.NullableFloat, back.NullableFloat);
        Assert.Equal(poco.NullableColor, back.NullableColor);
        Assert.Equal(poco.Blob, back.Blob);

        // BSON-side representations the official driver would also pick.
        Assert.Equal(BsonType.Int32, driver["Short"].BsonType);
        Assert.Equal(BsonType.Int32, driver["Char"].BsonType);
        Assert.Equal(BsonType.Int64, driver["UInt"].BsonType);
        Assert.Equal(BsonType.Int64, driver["ULong"].BsonType);
        Assert.Equal(BsonType.Double, driver["Float"].BsonType);
        Assert.Equal(BsonType.Binary, driver["Blob"].BsonType);
        Assert.Equal(BsonBinarySubType.Binary, driver["Blob"].AsBsonBinaryData.SubType);
        Assert.Equal(BsonBinarySubType.UuidStandard, driver["Guid"].AsBsonBinaryData.SubType);
        Assert.Equal(2, driver["Color"].AsInt32);
    }

    [Fact]
    public void Nulls_Are_Written_As_Bson_Null_And_Read_Back_As_Null()
    {
        var (back, driver) = RoundTrip(new EveryScalar { Id = 2, Text = null, NullableFloat = null, NullableColor = null, Blob = null });

        Assert.True(driver["Text"].IsBsonNull);
        Assert.True(driver["NullableFloat"].IsBsonNull);
        Assert.True(driver["NullableColor"].IsBsonNull);
        Assert.True(driver["Blob"].IsBsonNull);
        Assert.Null(back.Text);
        Assert.Null(back.NullableFloat);
        Assert.Null(back.NullableColor);
        Assert.Null(back.Blob);
    }

    [Fact]
    public void Collections_And_Dictionaries_Of_Every_Supported_Element_Type_Round_Trip()
    {
        var poco = new Collections
        {
            Id = 3,
            Guids = [Guid.NewGuid(), Guid.NewGuid()],
            Colors = [Color.Red, Color.Green],
            Prices = [1.5m, 2.25m],
            Floats = [0.5f, 1.5f],
            Matrix = [[1, 2], [3]],
            Addresses = [new Address { City = "Tel Aviv", Zip = 1 }, null!],
            Tags = ["a", "b"],
            ReadOnly = [7, 8, 9],
            Array = [4, 5],
            Blobs = [[1], [2, 3]],
            Scores = new() { ["x"] = 1.5, ["y"] = -2 },
            Ids = new() { ["k"] = Guid.NewGuid() },
            Lists = new() { ["l"] = [1, 2, 3] },
            Places = new() { ["home"] = new Address { City = "Haifa", Zip = 2 } },
            Flags = new() { ["on"] = true },
            ReadOnlyCounts = new Dictionary<string, long> { ["n"] = 1L << 35 },
        };

        var (back, driver) = RoundTrip(poco);

        Assert.Equal(poco.Guids, back.Guids);
        Assert.Equal(BsonBinarySubType.UuidStandard, driver["Guids"][0].AsBsonBinaryData.SubType);
        Assert.Equal(poco.Colors, back.Colors);
        Assert.Equal(1, driver["Colors"][0].AsInt32);
        Assert.Equal(poco.Prices, back.Prices);
        Assert.Equal(BsonType.Decimal128, driver["Prices"][0].BsonType);
        Assert.Equal(poco.Floats, back.Floats);
        Assert.Equal(poco.Matrix, back.Matrix);
        Assert.Equal("Tel Aviv", back.Addresses![0].City);
        Assert.Null(back.Addresses[1]);
        Assert.True(driver["Addresses"][1].IsBsonNull);
        Assert.Equal(poco.Tags, back.Tags);
        Assert.Equal(poco.ReadOnly, back.ReadOnly);
        Assert.Equal(poco.Array, back.Array);
        Assert.Equal(poco.Blobs, back.Blobs);
        Assert.Equal(BsonType.Binary, driver["Blobs"][0].BsonType);
        Assert.Equal(poco.Scores, back.Scores);
        Assert.Equal(1.5, driver["Scores"]["x"].AsDouble);
        Assert.Equal(poco.Ids, back.Ids);
        Assert.Equal(poco.Lists, back.Lists);
        Assert.Equal("Haifa", back.Places!["home"].City);
        Assert.Equal(poco.Flags, back.Flags);
        Assert.Equal(poco.ReadOnlyCounts["n"], back.ReadOnlyCounts!["n"]);
    }

    public class WithTimeSpan { public int Id { get; set; } public TimeSpan Span { get; set; } }
    public class WithNestedOffset { public int Id { get; set; } public List<Address>? Ok { get; set; } public Dictionary<string, DateTimeOffset>? Bad { get; set; } }
    public class WithObject { public int Id { get; set; } public object? Anything { get; set; } }
    public class WithIntKeys { public int Id { get; set; } public Dictionary<int, string>? ByNumber { get; set; } }
    public class Deep { public int Id { get; set; } public Inner? Inner { get; set; } }
    public class Inner { public List<WithTimeSpan>? Items { get; set; } }

    [Theory]
    [InlineData(typeof(WithTimeSpan), "WithTimeSpan.Span", "System.TimeSpan")]
    [InlineData(typeof(WithNestedOffset), "WithNestedOffset.Bad[key]", "System.DateTimeOffset")]
    [InlineData(typeof(WithObject), "WithObject.Anything", "System.Object")]
    [InlineData(typeof(WithIntKeys), "WithIntKeys.ByNumber", "Dictionary`2")]
    [InlineData(typeof(Deep), "Deep.Inner.Items[].Span", "System.TimeSpan")]
    public void Unsupported_Member_Types_Fail_Loudly_With_The_Offending_Path(Type entityType, string expectedPath, string expectedTypeFragment)
    {
        var ex = Assert.Throws<NotSupportedException>(() => BsonTypeModel.EnsureSupported(entityType));
        Assert.Contains(expectedPath, ex.Message);
        Assert.Contains(expectedTypeFragment, ex.Message);
    }

    [Fact]
    public void Serializing_An_Entity_With_An_Unsupported_Member_Throws_NotSupported_Not_TypeInitialization()
    {
        using var arena = new ArenaAllocator(1 << 16);
        var writer = new ArenaBsonWriter(arena);
        var ex = Assert.Throws<NotSupportedException>(() => DynamicBlittableSerializer<WithTimeSpan>.SerializeDelegate(ref writer, new WithTimeSpan { Id = 1 }));
        Assert.Contains("WithTimeSpan.Span", ex.Message);
    }

    public class Computed
    {
        public int Id { get; set; }
        public string First { get; set; } = "";
        public string Full => First + "!";
    }

    [Fact]
    public void Get_Only_Properties_Are_Not_Serialized()
    {
        var (_, driver) = RoundTrip(new Computed { Id = 1, First = "a" });
        Assert.False(driver.Contains("Full"));
    }

    public class UpperId { public int ID { get; set; } public string? Name { get; set; } }

    [Fact]
    public void Id_Property_Is_Matched_Case_Insensitively_Like_The_Id_Convention()
    {
        var (_, driver) = RoundTrip(new UpperId { ID = 42, Name = "x" });
        Assert.Equal(42, driver["_id"].AsInt32);
        Assert.False(driver.Contains("ID"));
    }
}

/// <summary>
/// An entity diffed against its own freshly-written snapshot must report no changes. The audit found
/// two cases where it did not: sub-millisecond DateTime ticks (BSON stores milliseconds) and null
/// strings (omitted on write, then <c>$set: null</c> on every save).
/// </summary>
public class SpuriousDirtyDetectionTests
{
    public class Entity
    {
        public int Id { get; set; }
        public DateTime When { get; set; }
        public DateTime? MaybeWhen { get; set; }
        public string? Name { get; set; }
        public List<int>? Numbers { get; set; }
        public Dictionary<string, string>? Meta { get; set; }
        public SerializerTypeCoverageTests.Address? Address { get; set; }
        public List<DateTime>? Stamps { get; set; }
        public byte[]? Blob { get; set; }
    }

    private static ArenaUpdateDefinitionBuilder Diff<T>(T entity, ArenaAllocator arena, Action<T>? mutate = null)
    {
        var writer = new ArenaBsonWriter(arena);
        DynamicBlittableSerializer<T>.SerializeDelegate(ref writer, entity);
        var snapshot = writer.Commit(arena);
        mutate?.Invoke(entity);
        var builder = new ArenaUpdateDefinitionBuilder(arena);
        DynamicBlittableSerializer<T>.BuildUpdateDelegate(entity, snapshot, ref builder, arena, default);
        return builder;
    }

    [Fact]
    public void Unchanged_Entity_With_Sub_Millisecond_DateTime_And_Nulls_Is_Not_Dirty()
    {
        using var arena = new ArenaAllocator(1 << 20);
        var now = DateTime.UtcNow;
        Assert.NotEqual(0, now.Ticks % TimeSpan.TicksPerMillisecond); // the premise: ticks below BSON precision

        var entity = new Entity { Id = 1, When = now, MaybeWhen = now, Name = null, Numbers = null, Meta = null, Address = null, Stamps = [now, now.AddDays(1)], Blob = null };
        var builder = Diff(entity, arena);

        Assert.False(builder.HasChanges);
    }

    [Fact]
    public void Unchanged_Entity_With_Populated_Collections_Is_Not_Dirty()
    {
        using var arena = new ArenaAllocator(1 << 20);
        var entity = new Entity
        {
            Id = 2, Name = "n", Numbers = [1, 2], Meta = new() { ["a"] = "b" },
            Address = new SerializerTypeCoverageTests.Address { City = "c", Zip = 1 }, Blob = [1, 2],
        };

        Assert.False(Diff(entity, arena).HasChanges);
    }

    [Fact]
    public void Real_Changes_Are_Still_Detected_Including_Null_Transitions()
    {
        using var arena = new ArenaAllocator(1 << 20);
        var entity = new Entity { Id = 3, Name = "n", Numbers = [1], Address = new SerializerTypeCoverageTests.Address { City = "c" }, When = DateTime.UtcNow };

        var builder = Diff(entity, arena, e =>
        {
            e.Name = null;
            e.Numbers = null;
            e.Address = null;
            e.When = e.When.AddMilliseconds(1);
            e.Blob = [9];
        });

        Assert.True(builder.HasChanges);
        var set = builder.Build().GetDocument("$set", arena);
        Assert.Equal(BlittableBsonConstants.BsonType.Null, ArenaBsonReader.GetElementType(set, Offset(set, "Name")));
        Assert.Equal(BlittableBsonConstants.BsonType.Null, ArenaBsonReader.GetElementType(set, Offset(set, "Numbers")));
        Assert.Equal(BlittableBsonConstants.BsonType.Null, ArenaBsonReader.GetElementType(set, Offset(set, "Address")));
        Assert.True(set.ContainsKey("When"));
        Assert.Equal(new byte[] { 9 }, set.GetBinary("Blob"));
    }

    [Fact]
    public void Property_Missing_From_Snapshot_Is_Set_Only_When_Non_Null()
    {
        using var arena = new ArenaAllocator(1 << 20);
        // Brownfield document: only _id present.
        var writer = new ArenaBsonWriter(arena);
        writer.WriteStartDocument();
        writer.WriteInt32("_id", 4);
        writer.WriteEndDocument();
        var snapshot = writer.Commit(arena);

        var entity = new Entity { Id = 4, Name = null, Numbers = [1] };
        var builder = new ArenaUpdateDefinitionBuilder(arena);
        DynamicBlittableSerializer<Entity>.BuildUpdateDelegate(entity, snapshot, ref builder, arena, default);

        var set = builder.Build().GetDocument("$set", arena);
        Assert.False(set.ContainsKey("Name"));     // null and absent are the same thing
        Assert.True(set.ContainsKey("Numbers"));   // real data the document lacks
        Assert.True(set.ContainsKey("When"));      // non-nullable value types always have a value
    }

    private static int Offset(BlittableBsonDocument doc, string name)
    {
        Assert.True(doc.TryGetElementOffset(name, out var offset), $"expected '{name}' in $set");
        return offset;
    }
}
