namespace MongoZen;

public record Conventions
{
    public IIdConvention IdConvention { get; init; } = new DefaultIdConvention();
    public IIdGenerator IdGenerator { get; init; } = new PrefixedStringIdGenerator();
    public TransactionSupportBehavior TransactionSupportBehavior { get; init; } = TransactionSupportBehavior.Throw;
    public bool DisableTransactions { get; init; } = false;
    public string? ConcurrencyPropertyName { get; init; } = "Version";
    public int QueryCacheSize { get; init; } = 1000;
}

