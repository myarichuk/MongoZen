using MongoDB.Driver.GridFS;
using System.Reflection;

namespace MongoZen.Bson;

public static class MetadataChecker
{
    public static void Check()
    {
        var assembly = typeof(IGridFSBucket).Assembly;
        Console.WriteLine($"Assembly: {assembly.FullName}");
        foreach (var type in assembly.GetExportedTypes())
        {
            if (type.Name.Contains("GridFSBucket") && type.Name.Contains("Extensions"))
            {
                Console.WriteLine($"Found Type: {type.FullName}");
                foreach (var m in type.GetMethods())
                {
                     if (m.Name.Contains("Async"))
                        Console.WriteLine($"  {m.Name}({string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name))})");
                }
            }
        }
    }
}
