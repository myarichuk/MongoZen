namespace MongoZen;

public enum TransactionSupportBehavior
{
    /// <summary>
    /// Throw when MongoDB does not support transactions.
    /// </summary>
    Throw,

    /// <summary>
    /// Simulate the unit of work without MongoDB transactions.
    /// </summary>
    Simulate,
}
