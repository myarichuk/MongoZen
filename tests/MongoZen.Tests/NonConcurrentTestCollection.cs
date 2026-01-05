// Copyright (c) MyProject. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace MongoZen.Tests;

/// <summary>
/// A test collection that runs tests without concurrency.
/// </summary>
[CollectionDefinition("NoConcurrency")]
public class NonConcurrentTestCollection : ICollectionFixture<object>
{
}
