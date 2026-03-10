using Microsoft.CodeAnalysis;

namespace MongoZen.SourceGenerator;

public static class Utils
{
    public static bool InheritsFrom(INamedTypeSymbol type, string shortName)
    {
        for (var current = type.BaseType; current != null; current = current.BaseType)
        {
            if (current.Name == shortName)
            {
                return true;
            }
        }

        return false;
    }
}