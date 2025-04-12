using Library.FilterUtils;
using MongoDB.Bson;
using MongoDB.Driver;

// ReSharper disable TooManyDeclarations
// ReSharper disable ClassTooBig
#pragma warning disable CS1591 // Missing XML comment for publicly visible type or member

namespace Library.Tests;

public class BsonFilterToLinqTranslatorTests
{
    private readonly FilterToLinqTranslator<Person> _translator = new();

    [Fact]
    public void EqFilter_ExpressionField_ShouldMatchCorrectly()
    {
        var filter = Builders<Person>.Filter.Eq(p => p.Name, "Alice");
        var expr = _translator.Translate(filter).Compile();

        Assert.True(expr(new Person { Name = "Alice" }));
        Assert.False(expr(new Person { Name = "Bob" }));
    }

    [Fact]
    public void EqFilter_StringField_ShouldMatchCorrectly()
    {
        var filter = Builders<Person>.Filter.Eq("Name", "Alice");
        var expr = _translator.Translate(filter).Compile();

        Assert.True(expr(new Person { Name = "Alice" }));
        Assert.False(expr(new Person { Name = "Bob" }));
    }
    
    [Fact]
    public void GtFilter_ShouldMatchCorrectly()
    {
        var filter = Builders<Person>.Filter.Gt(p => p.Age, 25);
        var expr = _translator.Translate(filter).Compile();

        Assert.True(expr(new Person { Age = 30 }));
        Assert.False(expr(new Person { Age = 25 }));
        Assert.False(expr(new Person { Age = 20 }));
    }

    [Fact]
    public void GteFilter_ShouldMatchCorrectly()
    {
        var filter = Builders<Person>.Filter.Gte(p => p.Age, 25);
        var expr = _translator.Translate(filter).Compile();

        Assert.True(expr(new Person { Age = 30 }));
        Assert.True(expr(new Person { Age = 25 }));
        Assert.False(expr(new Person { Age = 20 }));
    }

    [Fact]
    public void LtFilter_ShouldMatchCorrectly()
    {
        var filter = Builders<Person>.Filter.Lt(p => p.Age, 40);
        var expr = _translator.Translate(filter).Compile();

        Assert.True(expr(new Person { Age = 30 }));
        Assert.False(expr(new Person { Age = 40 }));
        Assert.False(expr(new Person { Age = 45 }));
    }

    [Fact]
    public void LteFilter_ShouldMatchCorrectly()
    {
        var filter = Builders<Person>.Filter.Lte(p => p.Age, 40);
        var expr = _translator.Translate(filter).Compile();

        Assert.True(expr(new Person { Age = 30 }));
        Assert.True(expr(new Person { Age = 40 }));        
        Assert.False(expr(new Person { Age = 45 }));
    }
    
    [Fact]
    public void RawEqFilterWithoutSubDocument_ShouldMatchCorrectly()
    {
        var filter = new BsonDocument("Name", "Alice").ToFilterDefinition<Person>();
        var expr = _translator.Translate(filter).Compile();

        Assert.True(expr(new Person { Name = "Alice" }));
        Assert.False(expr(new Person { Name = "NotAlice" }));
    }

    [Fact]
    public void CombinedEqAndGtFilter_ShouldMatchCorrectly()
    {
        var filter = Builders<Person>.Filter.And(
            Builders<Person>.Filter.Eq(p => p.Name, "Alice"),
            Builders<Person>.Filter.Gt(p => p.Age, 25)
        );

        var expr = _translator.Translate(filter).Compile();

        Assert.True(expr(new Person { Name = "Alice", Age = 30 }));
        Assert.False(expr(new Person { Name = "Alice", Age = 20 }));
        Assert.False(expr(new Person { Name = "Bob", Age = 30 }));
    }

    [Fact]
    public void NeFilter_ShouldMatchCorrectly()
    {
        var filter = Builders<Person>.Filter.Ne(p => p.Name, "Alice");
        var expr = _translator.Translate(filter).Compile();

        Assert.False(expr(new Person { Name = "Alice" }));
        Assert.True(expr(new Person { Name = "Bob" }));
    }

    [Fact]
    public void InFilter_ShouldMatchCorrectly()
    {
        var filter = Builders<Person>.Filter.In(p => p.Name, ["Alice", "Bob"]);
        var expr = _translator.Translate(filter).Compile();

        Assert.True(expr(new Person { Name = "Alice" }));
        Assert.True(expr(new Person { Name = "Bob" }));
        Assert.False(expr(new Person { Name = "Charlie" }));
    }

    [Fact]
    public void NinFilter_ShouldMatchCorrectly()
    {
        var filter = Builders<Person>.Filter.Nin(p => p.Name, ["Alice", "Bob"]);
        var expr = _translator.Translate(filter).Compile();

        Assert.False(expr(new Person { Name = "Alice" }));
        Assert.True(expr(new Person { Name = "Charlie" }));
    }

    [Fact]
    public void NotFilter_ShouldMatchCorrectly()
    {
        var filter = Builders<Person>.Filter.Not(Builders<Person>.Filter.Eq(p => p.Name, "Alice"));
        var expr = _translator.Translate(filter).Compile();

        Assert.False(expr(new Person { Name = "Alice" }));
        Assert.True(expr(new Person { Name = "Bob" }));
    }

    [Fact]
    public void ExistsFilter_ShouldMatchCorrectly()
    {
        var filter = new BsonDocument("Name", new BsonDocument("$exists", true)).ToFilterDefinition<Person>();
        var expr = _translator.Translate(filter).Compile();

        Assert.True(expr(new Person { Name = "Alice" }));
        //will only work correctly when handling null or missing fields explicitly
    }

    [Fact]
    public void TypeOperator_ShouldMatchCorrectly()
    {
        var filter = new BsonDocument("Age", new BsonDocument("$type", "int")).ToFilterDefinition<Person>();
        var expr = _translator.Translate(filter).Compile();

        Assert.True(expr(new Person { Age = 30 }));
    }

    [Fact]
    public void OrFilter_ShouldMatchCorrectly()
    {
        var filter = Builders<Person>.Filter.Or(
            Builders<Person>.Filter.Eq(p => p.Name, "Alice"),
            Builders<Person>.Filter.Lt(p => p.Age, 20)
        );

        var expr = _translator.Translate(filter).Compile();

        Assert.True(expr(new Person { Name = "Alice", Age = 30 }));
        Assert.True(expr(new Person { Name = "Charlie", Age = 10 }));
        Assert.False(expr(new Person { Name = "Charlie", Age = 30 }));
    }

    [Fact]
    public void AndFilter_ShouldMatchCorrectly()
    {
        var filter = Builders<Person>.Filter.And(
            Builders<Person>.Filter.Eq(p => p.Name, "Alice"),
            Builders<Person>.Filter.Lt(p => p.Age, 20)
        );

        var expr = _translator.Translate(filter).Compile();

        Assert.False(expr(new Person { Name = "Alice", Age = 30 }));
        Assert.True(expr(new Person { Name = "Alice", Age = 18 }));
        Assert.False(expr(new Person { Name = "Charlie", Age = 10 }));
        Assert.False(expr(new Person { Name = "Charlie", Age = 30 }));
    }
    
    [Fact]
    public void UnknownOperator_ShouldThrow()
    {
        var filter = new BsonDocument("Age", new BsonDocument("$mod", new BsonArray { 10, 1 })).ToFilterDefinition<Person>();
        Assert.Throws<NotSupportedException>(() => _translator.Translate(filter));
    }

    [Fact]
    public void UnknownField_ShouldThrow()
    {
        var filter = new BsonDocument("NonexistentField", 1).ToFilterDefinition<Person>();
        Assert.Throws<ArgumentException>(() => _translator.Translate(filter));
    }

    [Fact]
    public void AndFilterWithMultipleConditions_ShouldMatchCorrectly()
    {
        var filter = Builders<Person>.Filter.And(
            Builders<Person>.Filter.Eq(p => p.Name, "Alice"),
            Builders<Person>.Filter.Gte(p => p.Age, 25)
        );
        var expr = _translator.Translate(filter).Compile();

        Assert.True(expr(new Person { Name = "Alice", Age = 30 }));
        Assert.True(expr(new Person { Name = "Alice", Age = 25 }));
        Assert.False(expr(new Person { Name = "Alice", Age = 20 }));
        Assert.False(expr(new Person { Name = "Bob", Age = 30 }));
    }

    [Fact]
    public void OrFilterWithMultipleConditions_ShouldMatchCorrectly()
    {
        var filter = Builders<Person>.Filter.Or(
            Builders<Person>.Filter.Eq(p => p.Name, "Alice"),
            Builders<Person>.Filter.Lt(p => p.Age, 20)
        );
        var expr = _translator.Translate(filter).Compile();

        Assert.True(expr(new Person { Name = "Alice", Age = 50 }));
        Assert.True(expr(new Person { Name = "Charlie", Age = 15 }));
        Assert.False(expr(new Person { Name = "Charlie", Age = 30 }));
    }

    [Fact]
    public void NestedFieldFilter_ShouldMatchCorrectly()
    {
        var translator = new FilterToLinqTranslator<PersonWithAddress>();
        var filter = Builders<PersonWithAddress>.Filter.Eq(p => p.Address.City, "London");
        var expr = translator.Translate(filter).Compile();

        Assert.True(expr(new PersonWithAddress { Name = "Alice", Address = new Address { City = "London" } }));
        Assert.False(expr(new PersonWithAddress { Name = "Bob", Address = new Address { City = "Paris" } }));        
    }

    [Fact]
    public void RawNestedFieldFilter_ShouldMatchCorrectly()
    {
        var translator = new FilterToLinqTranslator<PersonWithAddress>();
        var filter = new BsonDocument("Address.City", "London").ToFilterDefinition<PersonWithAddress>();
        var expr = translator.Translate(filter).Compile();

        Assert.True(expr(new PersonWithAddress { Name = "Alice", Address = new Address { City = "London" } }));
        Assert.False(expr(new PersonWithAddress { Name = "Bob", Address = new Address { City = "Paris" } }));
    }

    [Fact]
    public void AndFilter_WithNestedOr_ShouldMatchCorrectly()
    {
        var filter = Builders<Person>.Filter.And(
            Builders<Person>.Filter.Eq(p => p.Name, "Alice"),
            Builders<Person>.Filter.Or(
                Builders<Person>.Filter.Lt(p => p.Age, 20),
                Builders<Person>.Filter.Eq(p => p.Age, 30)
            )
        );
        var expr = _translator.Translate(filter).Compile();

        Assert.True(expr(new Person { Name = "Alice", Age = 30 }));
        Assert.True(expr(new Person { Name = "Alice", Age = 10 }));
        Assert.False(expr(new Person { Name = "Alice", Age = 25 }));
        Assert.False(expr(new Person { Name = "Bob", Age = 30 }));
    }

    [Fact]
    public void NorFilter_ShouldMatchCorrectly()
    {
        // this will yield { "$nor" : [{ "Name" : "Alice" }, { "Age" : { "$lt" : 20 } }] }
        var filter = Builders<Person>.Filter.Not(
            Builders<Person>.Filter.Or(
            Builders<Person>.Filter.Eq(p => p.Name, "Alice"),
            Builders<Person>.Filter.Lt(p => p.Age, 20)));

        var expr = _translator.Translate(filter).Compile();

        Assert.False(expr(new Person { Name = "Alice", Age = 30 }));
        Assert.False(expr(new Person { Name = "Charlie", Age = 15 }));
        Assert.True(expr(new Person { Name = "Charlie", Age = 30 }));
    }

    private class Person
    {
        public string Name { get; init; }

        public int Age { get; init; }
    }

    private class Address
    {
        public string City { get; init; }
    }

    private class PersonWithAddress
    {
        public string Name { get; set; }

        public Address Address { get; init; }
    }
}