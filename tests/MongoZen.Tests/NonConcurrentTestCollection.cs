namespace MongoZen.Tests;

[CollectionDefinition("NoConcurrency")]
public class NonConcurrentTestCollection : ICollectionFixture<object> { }