using System.Linq.Expressions;
using BenchmarkDotNet.Attributes;
using MongoDB.Bson;
using MongoZen.FilterUtils.ExpressionTranslators;

namespace MongoZen.Benchmarks;

[MemoryDiagnoser]
public class FilterTranslatorBenchmarks
{
    private FilterElementTranslatorBase _translator;
    private ParameterExpression _param;
    private BsonValue _value;

    [GlobalSetup]
    public void Setup()
    {
        _translator = new EqFilterElementTranslator(); // Derived from FilterElementTranslatorBase
        _param = Expression.Parameter(typeof(TestEntity), "x");
        _value = BsonValue.Create("test");
    }

    [Benchmark]
    public Expression TranslateNestedArrayField()
    {
        return _translator.Handle("Items.Tags.Name", _value, _param);
    }
}

public class TestEntity
{
    public List<TestItem> Items { get; set; }
}

public class TestItem
{
    public List<TestTag> Tags { get; set; }
}

public class TestTag
{
    public string Name { get; set; }
}
