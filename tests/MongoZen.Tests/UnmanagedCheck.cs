using MongoZen.Bson;
using MongoZen.ChangeTracking;
using Xunit;

namespace MongoZen.Tests;

public class UnmanagedCheck
{
    [Fact]
    public void EntityEntry_Is_Unmanaged()
    {
        UnmanagedOnly<EntityEntry>();
    }

    [Fact]
    public void DocId_Is_Unmanaged()
    {
        UnmanagedOnly<DocId>();
    }

    private void UnmanagedOnly<T>() where T : unmanaged
    {
        Assert.True(true);
    }
}
