using MongoDB.Bson;
using MongoZen.Bson;
using MongoZen.ChangeTracking;
using SharpArena.Allocators;
using Xunit;

namespace MongoZen.Tests;

public class ArenaUpdateDefinitionBuilderTests
{
    [Fact]
    public void Builder_Should_Generate_Correct_Set_Bson()
    {
        using var arena = new ArenaAllocator(1024);
        var builder = new ArenaUpdateDefinitionBuilder(arena);
        
        builder.Set("Name", "Updated");
        builder.Set("Age", 30);
        
        var doc = builder.Build();
        
        Assert.True(doc.ContainsKey("$set"));
        var setDoc = doc.GetDocument("$set", arena);
        
        Assert.Equal("Updated", setDoc.GetString("Name"));
        Assert.Equal(30, setDoc.GetInt32("Age"));
    }

    [Fact]
    public void Builder_Should_Handle_Unset()
    {
        using var arena = new ArenaAllocator(1024);
        var builder = new ArenaUpdateDefinitionBuilder(arena);
        
        builder.Unset("OldField");
        
        var doc = builder.Build();
        
        Assert.True(doc.ContainsKey("$unset"));
        var unsetDoc = doc.GetDocument("$unset", arena);
        Assert.Equal(1, unsetDoc.GetInt32("OldField"));
    }

    [Fact]
    public void Builder_Should_Handle_Mixed_Set_And_Unset()
    {
        using var arena = new ArenaAllocator(1024);
        var builder = new ArenaUpdateDefinitionBuilder(arena);
        
        // Use separate builders for now if mixed order is tricky, 
        // OR ensure EnsureSetStarted handles closing Unset.
        // Actually, let's fix EnsureSetStarted to handle closing Unset too.
        builder.Set("Name", "New");
        builder.Unset("Deleted");
        
        var doc = builder.Build();
        
        Assert.True(doc.ContainsKey("$set"));
        Assert.True(doc.ContainsKey("$unset"));
    }

    [Fact]
    public void Builder_Should_Return_Default_If_No_Changes()
    {
        using var arena = new ArenaAllocator(1024);
        var builder = new ArenaUpdateDefinitionBuilder(arena);
        
        var doc = builder.Build();
        
        Assert.True(doc.IsDefault);
    }
}
