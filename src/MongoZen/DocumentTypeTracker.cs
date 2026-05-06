using System.Reflection;

namespace MongoZen;

/// <summary>
/// Utility to find types marked as Documents within an assembly.
/// </summary>
public static class DocumentTypeTracker
{
    /// <summary>
    /// Returns all types in the assembly that are explicitly marked with [Document]
    /// or belong to a namespace marked with [GenerateDocumentShadowsForNamespace].
    /// </summary>
    public static IEnumerable<Type> GetDocumentTypes(Assembly assembly)
    {
        var namespaces = assembly.GetCustomAttributes<GenerateDocumentShadowsForNamespaceAttribute>()
            .Select(a => a.Namespace)
            .ToList();

        return assembly.GetTypes().Where(t => 
            t.GetCustomAttribute<DocumentAttribute>() != null ||
            (t.Namespace != null && namespaces.Any(ns => t.Namespace == ns || t.Namespace.StartsWith(ns + ".")))
        );
    }
}
