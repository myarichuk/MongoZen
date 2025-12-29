using System.Reflection;

namespace MongoZen.FilterUtils;

/// <summary>
/// Discovers filter element translators from assemblies.
/// </summary>
public static class FilterElementTranslatorDiscovery
{
    /// <summary>
    /// Discovers filter element translators from the MongoZen assembly.
    /// </summary>
    /// <returns>The discovered translators.</returns>
    public static IEnumerable<IFilterElementTranslator> DiscoverFromMongoZen() =>
        typeof(FilterElementTranslatorDiscovery).Assembly.GetTypes()
            .Where(t => t is { IsAbstract: false, IsInterface: false, IsGenericType: false } && typeof(IFilterElementTranslator).IsAssignableFrom(t))
            .Select(t => (IFilterElementTranslator?)Activator.CreateInstance(t))
            .Where(t => t != null) // just in case
            .Cast<IFilterElementTranslator>();

    /// <summary>
    /// Discovers filter element translators from the specified assemblies.
    /// </summary>
    /// <param name="assemblies">Assemblies to scan. If none are provided, the executing assembly is used.</param>
    /// <returns>The discovered translators.</returns>
    public static IEnumerable<IFilterElementTranslator> DiscoverFrom(params Assembly[] assemblies)
    {
        if (assemblies is not { Length: not 0 })
        {
            assemblies = [Assembly.GetExecutingAssembly()];
        }

        return assemblies
            .SelectMany(a => a.GetTypes())
            .Where(t => t is { IsAbstract: false, IsInterface: false } && typeof(IFilterElementTranslator).IsAssignableFrom(t))
            .Select(t => (IFilterElementTranslator?)Activator.CreateInstance(t))
            .Where(t => t != null) // just in case
            .Cast<IFilterElementTranslator>();
    }
}
