using EphemeralMongo;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;
using MongoZen;

var options = new MongoRunnerOptions
{
    UseSingleNodeReplicaSet = true,
    StandardErrorLogger = Console.WriteLine,
};

using var runner = await MongoRunner.RunAsync(options);
using var mongoClient = new MongoClient(runner.ConnectionString);
var database = mongoClient.GetDatabase($"test_{Guid.NewGuid()}");

Console.WriteLine($"MongoDB is running at: {runner.ConnectionString}");

var dbContextOptions = new DbContextOptions(database);

using var dbContext = new MyDbContext(dbContextOptions);

var person = new Person
{
    Id = Guid.NewGuid().ToString(),
    Name = "Alice",
    Age = 30,
};
var person2 = new Person
{
    Id = Guid.NewGuid().ToString(),
    Name = "Bob",
    Age = 32,
};

var person3 = new Person
{
    Id = Guid.NewGuid().ToString(),
    Name = "Charlie",
    Age = 25,
};

var session = dbContext.StartSession();
session.People.Add(person);
session.People.Add(person2);
session.People.Add(person3);

await session.SaveChangesAsync();

Console.WriteLine("Inserted data into the 'People' collection.");

var people = await dbContext.People.QueryAsync(p => true);
Console.WriteLine("Queried in 'People' collection:");
foreach (var p in people)
{
    Console.WriteLine($"Id: {p.Id}, Name: {p.Name}, Age: {p.Age}");
}

var olderThan30People = await dbContext.People.QueryAsync(p => p.Age > 30);
Console.WriteLine("Queried in 'People' collection for people older than 30:");
foreach (var p in olderThan30People)
{
    Console.WriteLine($"Id: {p.Id}, Name: {p.Name}, Age: {p.Age}");
}

public class MyDbContext : DbContext
{
    public IDbSet<Person> People { get; set; }

    public MyDbContext(DbContextOptions options) : base(options) { }
}

// Define the Person entity with ID handling
[BsonIgnoreExtraElements]
public class Person
{
    [BsonId]
    public string Id { get; set; }

    public string Name { get; set; }

    public int Age { get; set; }
}