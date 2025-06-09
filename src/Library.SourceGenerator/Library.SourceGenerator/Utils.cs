using Microsoft.CodeAnalysis;

namespace MongoFlow.SourceGenerator;

public static class Utils
{
    public static bool InheritsFrom(INamedTypeSymbol type, string fullMetadataName, Compilation compilation)
    {
        var baseType = compilation.GetTypeByMetadataName(fullMetadataName);
        if (baseType == null)
        {
            return false;
        }

        for (var current = type.BaseType; current != null; current = current.BaseType)
        {
            // note: makes better sense use symbol comparer than string comparer
            if (SymbolEqualityComparer.Default.Equals(current, baseType))
            {
                return true;
            }
        }

        return false;
    }

}