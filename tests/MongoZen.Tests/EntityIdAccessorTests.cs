using System;
using System.Reflection;
using MongoDB.Bson;
using Xunit;

namespace MongoZen.Tests;

public class EntityIdAccessorTests
{
    public class SimpleIntEntity { public int Id { get; set; } }
    public class SimpleStringEntity { public string Id { get; set; } = string.Empty; }
    public class SimpleObjectIdEntity { public ObjectId Id { get; set; } }
    public class SimpleGuidEntity { public Guid Id { get; set; } }

    private class TestIdConvention : IIdConvention
    {
        public PropertyInfo? ResolveIdProperty<TEntity>() => typeof(TEntity).GetProperty("Id");
    }

    private readonly TestIdConvention _convention = new();

    [Fact]
    public void Should_Access_Int_Id()
    {
        var entity = new SimpleIntEntity { Id = 42 };
        var getter = EntityIdAccessor<SimpleIntEntity>.GetAccessor(_convention);
        var setter = EntityIdAccessor<SimpleIntEntity>.GetSetter(_convention);
        var docIdGetter = EntityIdAccessor<SimpleIntEntity>.GetDocIdAccessor(_convention);

        Assert.Equal(42, getter(entity));
        Assert.Equal(DocId.FromInt32(42), docIdGetter(entity));

        setter(entity, 100);
        Assert.Equal(100, entity.Id);
    }

    [Fact]
    public void Should_Access_String_Id()
    {
        var entity = new SimpleStringEntity { Id = "test-id" };
        var getter = EntityIdAccessor<SimpleStringEntity>.GetAccessor(_convention);
        var docIdGetter = EntityIdAccessor<SimpleStringEntity>.GetDocIdAccessor(_convention);

        Assert.Equal("test-id", getter(entity));
        Assert.Equal(DocId.FromString("test-id"), docIdGetter(entity));
    }

    [Fact]
    public void Should_Access_ObjectId_Id()
    {
        var oid = ObjectId.GenerateNewId();
        var entity = new SimpleObjectIdEntity { Id = oid };
        var getter = EntityIdAccessor<SimpleObjectIdEntity>.GetAccessor(_convention);
        var docIdGetter = EntityIdAccessor<SimpleObjectIdEntity>.GetDocIdAccessor(_convention);

        Assert.Equal(oid, getter(entity));
        Assert.Equal(DocId.FromObjectId(oid), docIdGetter(entity));
    }

    [Fact]
    public void Should_Access_Guid_Id()
    {
        var guid = Guid.NewGuid();
        var entity = new SimpleGuidEntity { Id = guid };
        var getter = EntityIdAccessor<SimpleGuidEntity>.GetAccessor(_convention);
        var docIdGetter = EntityIdAccessor<SimpleGuidEntity>.GetDocIdAccessor(_convention);

        Assert.Equal(guid, getter(entity));
        Assert.Equal(DocId.FromGuid(guid), docIdGetter(entity));
    }
}
