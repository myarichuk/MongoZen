namespace MongoZen;

/// <summary>
/// Defines customizable conventions used by MongoZen components.
/// </summary>
public record Conventions
{
    /// <summary>
    /// Gets or sets the convention used to resolve entity identifiers.
    /// </summary>
    public IIdConvention IdConvention { get; set; } = new DefaultIdConvention();
}
