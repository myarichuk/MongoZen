namespace MongoFlow.Tests;

[CollectionDefinition("NoConcurrency")]
public class NonConcurrentTestCollection : ICollectionFixture<object> { }