namespace MongoZen;

internal static class GlobalIdConventionProvider
{
    public static IIdConvention Convention { get; set; } = new DefaultIdConvention();
}
