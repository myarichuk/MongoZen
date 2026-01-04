using MongoZen.FilterUtils;
using MongoDB.Bson;
using MongoDB.Driver;

// ReSharper disable TooManyDeclarations
// ReSharper disable ClassTooBig
#pragma warning disable CS1591 // Missing XML comment for publicly visible type or member

namespace MongoZen.Tests;

public class FilterToLinqTests
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
            Builders<Person>.Filter.Gt(p => p.Age, 25));

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
    public void ExistsFilter_ShouldThrowNotSupported()
    {
        var filter = new BsonDocument("Name", new BsonDocument("$exists", true)).ToFilterDefinition<Person>();
        Assert.Throws<NotSupportedException>(() =>  _translator.Translate(filter).Compile());
    }

    [Fact]
    public void TypeOperator_OnStronglyTyped_ShouldMatchCorrectly()
    {
        var filter = new BsonDocument("Age", new BsonDocument("$type", "int")).ToFilterDefinition<Person>();
        var expr = _translator.Translate(filter).Compile();

        Assert.True(expr(new Person { Age = 30 }));
    }

    [Fact]
    public void TypeOperator_OnObject_ShouldMatchCorrectly()
    {
        var filter = new BsonDocument("Metadata", new BsonDocument("$type", "int")).ToFilterDefinition<Order>();
        var translator = new FilterToLinqTranslator<Order>();
        var expr = translator.Translate(filter).Compile();

        Assert.True(expr(new Order { Metadata = 30 }));
        Assert.False(expr(new Order { Metadata = "New-York City" }));
        Assert.False(expr(new Order { Metadata = new Person { Age = 30 } }));
    }
    
    [Fact]
    public void OrFilter_ShouldMatchCorrectly()
    {
        var filter = Builders<Person>.Filter.Or(
            Builders<Person>.Filter.Eq(p => p.Name, "Alice"),
            Builders<Person>.Filter.Lt(p => p.Age, 20));

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
            Builders<Person>.Filter.Lt(p => p.Age, 20));

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
            Builders<Person>.Filter.Gte(p => p.Age, 25));
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
            Builders<Person>.Filter.Lt(p => p.Age, 20));
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
                Builders<Person>.Filter.Eq(p => p.Age, 30)));
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
    
    [Fact]
    public void ElemMatch_SimpleEquality_ShouldMatchCorrectly()
    {
        var translator = new FilterToLinqTranslator<Order>();

        var filter = Builders<Order>.Filter.ElemMatch(
            o => o.Lines,
            Builders<OrderLine>.Filter.Eq(l => l.Product, "Apples"));

        var expr = translator.Translate(filter).Compile();

        var matchingOrder = new Order
        {
            Lines =
            [
                new OrderLine(product: "Apples", quantity: 2),
                new OrderLine(product: "Bananas", quantity: 3)
            ],
        };

        var nonMatchingOrder = new Order
        {
            Lines = [new OrderLine(product: "Oranges", quantity: 1)],
        };

        Assert.True(expr(matchingOrder));
        Assert.False(expr(nonMatchingOrder));
    }

    [Fact]
    public void ElemMatch_MultipleConditions_ShouldMatchCorrectly()
    {
        var translator = new FilterToLinqTranslator<Order>();

        var filter = Builders<Order>.Filter.ElemMatch(
            o => o.Lines,
            Builders<OrderLine>.Filter.And(
                Builders<OrderLine>.Filter.Gt(l => l.Quantity, 5),
                Builders<OrderLine>.Filter.Lt(l => l.Price, 10)));

        var expr = translator.Translate(filter).Compile();

        var matchingOrder = new Order
        {
            Lines =
            [
                new OrderLine(product: "Apples", quantity: 6, price: 9.99m),
                new OrderLine(product: "Bananas", quantity: 3, price: 5.00m)
            ],
        };

        var nonMatchingOrder = new Order
        {
            Lines =
            [
                new OrderLine(product: "Apples", quantity: 4, price: 9.99m),
                new OrderLine(product: "Bananas", quantity: 6, price: 11.00m)
            ],
        };

        Assert.True(expr(matchingOrder));
        Assert.False(expr(nonMatchingOrder));
    }

    [Fact]
    public void ElemMatch_WithOrCondition_ShouldMatchCorrectly()
    {
        var translator = new FilterToLinqTranslator<Order>();

        var filter = Builders<Order>.Filter.ElemMatch(
            o => o.Lines,
            Builders<OrderLine>.Filter.Or(
                Builders<OrderLine>.Filter.Eq(l => l.Product, "Apples"),
                Builders<OrderLine>.Filter.Gt(l => l.Quantity, 10)));

        var expr = translator.Translate(filter).Compile();

        var matchingOrder1 = new Order
        {
            Lines = [new OrderLine(product: "Apples", quantity: 2)],
        };

        var matchingOrder2 = new Order
        {
            Lines = [new OrderLine(product: "Bananas", quantity: 20)],
        };

        var nonMatchingOrder = new Order
        {
            Lines = [new OrderLine(product: "Oranges", quantity: 1)],
        };

        Assert.True(expr(matchingOrder1));
        Assert.True(expr(matchingOrder2));
        Assert.False(expr(nonMatchingOrder));
    }

    [Fact]
    public void ElemMatch_UnsupportedOperator_ShouldThrow()
    {
        var translator = new FilterToLinqTranslator<Order>();

        var filter = new BsonDocument("Lines", new BsonDocument(
            "$elemMatch",
            new BsonDocument("Quantity", new BsonDocument("$mod", new BsonArray { 2, 0 })))).ToFilterDefinition<Order>();

        Assert.Throws<NotSupportedException>(() => translator.Translate(filter));
    }
    
    [Fact]
    public void RegexFilter_SimpleMatch_ShouldWorkCorrectly()
    {
        var filter = Builders<Person>.Filter.Regex(p => p.Name, new BsonRegularExpression("^Al.*"));

        var expr = _translator.Translate(filter).Compile();

        Assert.True(expr(new Person { Name = "Alice" }));
        Assert.True(expr(new Person { Name = "Alfred" }));
        Assert.False(expr(new Person { Name = "Bob" }));
    }
    
    [Fact]
    public void RegexFilter_CaseSensitive_ShouldRespectCase()
    {
        var filter = Builders<Person>.Filter.Regex(p => p.Name, new BsonRegularExpression("^alice$", "i"));

        var expr = _translator.Translate(filter).Compile();

        Assert.True(expr(new Person { Name = "Alice" }));
        Assert.True(expr(new Person { Name = "alice" }));
        Assert.False(expr(new Person { Name = "Bob" }));
    }

    [Fact]
    public void RawRegexFilter_ShouldTranslateCorrectly()
    {
        var filter = new BsonDocument("Name", new BsonDocument("$regex", "^A.*e$")).ToFilterDefinition<Person>();

        var expr = _translator.Translate(filter).Compile();

        Assert.True(expr(new Person { Name = "Alice" }));
        Assert.True(expr(new Person { Name = "Anne" }));
        Assert.False(expr(new Person { Name = "Bob" }));
    }

    [Fact]
    public void UnsupportedRegexOption_ShouldThrow()
    {
        var filter = new BsonDocument("Name", new BsonDocument("$regex", "^Alice").Add("$options", "x"));

        Assert.Throws<NotSupportedException>(() => _translator.Translate(filter));
    }
    
    [Fact]
    public void AllFilter_WithSingleElement_ShouldMatchCorrectly()
    {
        var filter = Builders<Person>.Filter.All(p => p.Tags, ["blogger"]);
        var expr = _translator.Translate(filter).Compile();

        Assert.True(expr(new Person { Tags = [null!, "blogger"] }));
        Assert.True(expr(new Person { Tags = ["blogger"] }));
        Assert.True(expr(new Person { Tags = ["blogger", "dev"] }));
        Assert.True(expr(new Person { Tags = ["promoter", "blogger", "dev"] }));
        Assert.False(expr(new Person { Tags = null }));
        Assert.False(expr(new Person { Tags = [] }));
        Assert.False(expr(new Person { Tags = ["dev"] }));
    }
    
    [Fact]
    public void AllFilter_WithMultiElement_ShouldMatchCorrectly()
    {
        var filter = Builders<Person>.Filter.All(p => p.Tags, ["dev", "blogger"]);
        var expr = _translator.Translate(filter).Compile();

        Assert.True(expr(new Person { Tags = [null!, "blogger", null, "dev"] }));
        Assert.False(expr(new Person { Tags = ["blogger"] }));
        Assert.True(expr(new Person { Tags = ["blogger", "dev"] }));
        Assert.True(expr(new Person { Tags = ["promoter", "blogger", "seo", "dev"] }));
        Assert.False(expr(new Person { Tags = null }));
        Assert.False(expr(new Person { Tags = [] }));
        Assert.False(expr(new Person { Tags = ["dev"] }));
    }
    
    [Fact]
    public void AllFilter_OnNullProperty_ShouldNotMatch()
    {
        var filter = Builders<Person>.Filter.All(p => p.Tags, ["dev"]);
        var expr = _translator.Translate(filter).Compile();

        Assert.False(expr(new Person { Tags = null }));
        Assert.False(expr(new Person { Tags = [] }));
    }

    [Fact]
    public void AllFilter_OnNonCollection_ShouldThrow()
    {
        var filter = new BsonDocument("Age", new BsonDocument("$all", new BsonArray { 30 })).ToFilterDefinition<Person>();
        Assert.Throws<InvalidOperationException>(() => _translator.Translate(filter));
    }
    
    [Fact]
    public void AllFilter_ShouldMatch_WhenAllItemsExist()
    {
        var filter = Builders<Person>.Filter.All(p => p.Tags, ["dev", "blogger"]);

        var expr = _translator.Translate(filter).Compile();

        Assert.True(expr(new Person { Tags = ["dev", "blogger", "coffee lover"] }));
        Assert.True(expr(new Person { Tags = ["blogger", "dev"] }));

        Assert.False(expr(new Person { Tags = ["dev"] }));
        Assert.False(expr(new Person { Tags = ["blogger"] }));
        Assert.False(expr(new Person { Tags = ["writer", "reader"] }));
    }
    
    [Fact]
    public void AllFilter_WithEmptyArray_ShouldAlwaysMatch()
    {
        var filter = Builders<Person>.Filter.All(p => p.Tags, Array.Empty<string>());
        var expr = _translator.Translate(filter).Compile();

        Assert.True(expr(new Person { Tags = [] }));
        Assert.True(expr(new Person { Tags = ["random"] }));
    }

    public class Order
    {
        public string Id { get; set; } = null!;

        public object Metadata { get; set; } = null!;

        public List<OrderLine> Lines { get; set; } = null!;
    }

    public class OrderLine
    {
        public OrderLine()
        {
        }

        public OrderLine(string product, int quantity)
        {
            Product = product;
            Quantity = quantity;
        }

        public OrderLine(string product, int quantity, decimal price) : this()
        {
            Product = product;
            Quantity = quantity;
            Price = price;
        }

        public string Product { get; set; } = null!;

        public int Quantity { get; set; }

        public decimal Price { get; set; }
    }

    private class Person
    {
        public string Name { get; init; } = null!;

        public int Age { get; init; }

        public List<string?>? Tags { get; set; } = new();
    }

    private class Address
    {
        public string City { get; init; } = null!;
    }

    private class PersonWithAddress
    {
        public string Name { get; set; } = null!;

        public Address Address { get; init; } = null!;
    }
}
