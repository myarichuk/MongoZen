namespace MongoZen;

public record Conventions
{
    public IIdConvention IdConvention { get; set; } = new DefaultIdConvention();
}