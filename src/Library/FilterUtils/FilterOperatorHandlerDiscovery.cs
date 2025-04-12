using System.Reflection;

namespace Library.FilterUtils;

public static class FilterElementTranslatorDiscovery
{
    public static IEnumerable<IFilterElementTranslator> DiscoverFromLibrary() =>
        typeof(FilterElementTranslatorDiscovery).Assembly.GetTypes()
            .Where(t => t is { IsAbstract: false, IsInterface: false, IsGenericType: false } && typeof(IFilterElementTranslator).IsAssignableFrom(t))
            .Select(t => (IFilterElementTranslator)Activator.CreateInstance(t));

    public static IEnumerable<IFilterElementTranslator> DiscoverFrom(params Assembly[] assemblies)
    {
        if (assemblies is not { Length: not 0 })
        {
            assemblies = [Assembly.GetExecutingAssembly()];
        }

        return assemblies
            .SelectMany(a => a.GetTypes())
            .Where(t => t is { IsAbstract: false, IsInterface: false } && typeof(IFilterElementTranslator).IsAssignableFrom(t))
            .Select(t => (IFilterElementTranslator)Activator.CreateInstance(t));
    }
}