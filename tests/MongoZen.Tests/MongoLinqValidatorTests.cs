using System.Linq.Expressions;
using MongoZen;

#pragma warning disable CS1591 // Missing XML comment for publicly visible type or member

namespace MongoZen.Tests;

public class MongoLinqValidatorTests
{
    private record Person(string Name, int Age);

    [Fact]
    public void ValidExpression_ShouldNotThrow()
    {
        Expression<Func<Person, bool>> expr = p => p.Age > 30 && p.Name == "Alice";

        var ex = Record.Exception(() =>
            MongoLinqValidator.ValidateAndThrowIfNeeded(expr));

        Assert.Null(ex);
    }

    [Fact]
    public void MethodCall_ShouldThrow()
    {
        Expression<Func<Person, bool>> expr = p => p.Name.ToLower() == "alice";

        var ex = Assert.Throws<NotSupportedException>(() =>
            MongoLinqValidator.ValidateAndThrowIfNeeded(expr));

        Assert.Contains("Method call", ex.Message);
    }

    [Fact]
    public void MethodCall2_ShouldThrow()
    {
        Expression<Func<Person, bool>> expr = p => p.Name.Equals("alice", StringComparison.CurrentCultureIgnoreCase);

        var ex = Assert.Throws<NotSupportedException>(() =>
            MongoLinqValidator.ValidateAndThrowIfNeeded(expr));

        Assert.Contains("Method call", ex.Message);
    }

    [Fact]
    public void MethodCall_Contains_ShouldNotThrow() // supported by Mongo driver Linq provider
    {
        Expression<Func<Person, bool>> expr = p => p.Name.Contains('a');

        var ex = Record.Exception(() =>
            MongoLinqValidator.ValidateAndThrowIfNeeded(expr));

        Assert.Null(ex);
    }

    [Fact]
    public void TypeCast_ShouldThrow()
    {
        // ReSharper disable once RedundantCast --> needed for the test, its NOT redundant here!
        Expression<Func<Person, bool>> expr = p => (object)p.Age == "30";

        var ex = Assert.Throws<NotSupportedException>(() =>
            MongoLinqValidator.ValidateAndThrowIfNeeded(expr));

        Assert.Contains("Type casts", ex.Message);
    }

    [Fact]
    public void CapturedClosureConstant_ShouldThrow()
    {
        var age = 42;
        Expression<Func<Person, bool>> expr = p => p.Age == age;

        var ex = Assert.Throws<NotSupportedException>(() =>
            MongoLinqValidator.ValidateAndThrowIfNeeded(expr));

        Assert.Contains("Captured constants", ex.Message);
    }

    [Fact]
    public void DelegateInvocation_ShouldThrow()
    {
        Func<int, bool> predicate = x => x > 10;
        Expression<Func<Person, bool>> expr = p => predicate(p.Age);

        var ex = Assert.Throws<NotSupportedException>(() =>
            MongoLinqValidator.ValidateAndThrowIfNeeded(expr));

        Assert.Contains("Invoked expressions", ex.Message);
    }

    [Fact]
    public void SimplePropertyAccess_ShouldNotThrow()
    {
        Expression<Func<Person, object>> expr = p => p.Name;

        var ex = Record.Exception(() =>
            MongoLinqValidator.ValidateAndThrowIfNeeded(expr));

        Assert.Null(ex);
    }

    [Fact]
    public void BinaryAndAlsoOperation_ShouldNotThrow()
    {
        Expression<Func<Person, bool>> expr = p => p.Age > 20 && p.Age < 60;

        var ex = Record.Exception(() =>
            MongoLinqValidator.ValidateAndThrowIfNeeded(expr));

        Assert.Null(ex);
    }
}