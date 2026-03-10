namespace MongoZen;

public enum TransactionSupportBehavior
{
    /// <summary>
    /// Simulate the unit of work without MongoDB transactions.
    /// </summary>
    Simulate,

    /// <summary>
    /// Throw when MongoDB does not support transactions.
    /// </summary>
    Throw,
}
