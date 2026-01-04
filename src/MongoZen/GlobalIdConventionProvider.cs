namespace MongoZen;

internal static class GlobalIdConventionProvider
{
    public static IIdConvention Convention { get; private set; } = new DefaultIdConvention();
}
