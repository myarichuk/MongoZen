using System.Reflection;

namespace MongoZen.FilterUtils;

public static class FilterElementTranslatorDiscovery
{
    public static IEnumerable<IFilterElementTranslator> DiscoverFromMongoZen() =>
        typeof(FilterElementTranslatorDiscovery).Assembly.GetTypes()
            .Where(t => t is { IsAbstract: false, IsInterface: false, IsGenericType: false } && typeof(IFilterElementTranslator).IsAssignableFrom(t))
            .Select(t => (IFilterElementTranslator?)Activator.CreateInstance(t))
            .Where(t => t != null) // just in case
            .Cast<IFilterElementTranslator>();

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
