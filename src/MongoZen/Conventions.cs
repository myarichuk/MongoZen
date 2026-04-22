namespace MongoZen;

public record Conventions
{
    public IIdConvention IdConvention { get; set; } = new DefaultIdConvention();

    public IIdGenerator IdGenerator { get; set; } = new PrefixedStringIdGenerator();

    public TransactionSupportBehavior TransactionSupportBehavior { get; set; } = TransactionSupportBehavior.Throw;
}
