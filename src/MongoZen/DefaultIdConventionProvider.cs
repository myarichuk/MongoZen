namespace MongoZen;

internal static class DefaultIdConventionProvider
{
    public static IIdConvention Convention { get; set; } = new DefaultIdConvention();
}