using Microsoft.CodeAnalysis;

namespace MongoZen.SourceGenerator;

/// <summary>
/// Utility helpers for source generators.
/// </summary>
public static class Utils
{
    /// <summary>
    /// Determines whether a symbol inherits from the specified base type.
    /// </summary>
    /// <param name="type">The symbol to inspect.</param>
    /// <param name="fullMetadataName">The metadata name of the base type.</param>
    /// <param name="compilation">The compilation containing the type metadata.</param>
    /// <returns><see langword="true"/> when the type inherits from the base type; otherwise <see langword="false"/>.</returns>
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
