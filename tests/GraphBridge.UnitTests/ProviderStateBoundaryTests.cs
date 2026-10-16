using GraphBridge.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore;

namespace GraphBridge.UnitTests;

public sealed class ProviderStateBoundaryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Database_value_refresh_rejects_unsupported_projection_without_mutating_tracker_or_using_driver(bool reload)
    {
        var driver = ProviderTestDriver.Create();
        await using var context = new ProviderModelContext(driver, b => b.Entity<Person>().ToGraphNode("RefreshPerson"));
        var person = new Person { Id = Guid.NewGuid(), Name = "original" };
        context.Attach(person);
        person.Name = "pending";
        context.ChangeTracker.DetectChanges();

        await Assert.ThrowsAsync<Neo4jQueryTranslationException>(async () =>
        {
            if (reload)
                await context.Entry(person).ReloadAsync();
            else
                await context.Entry(person).GetDatabaseValuesAsync();
        });

        Assert.Equal("pending", person.Name);
        Assert.Equal("original", context.Entry(person).Property(p => p.Name).OriginalValue);
        Assert.Equal(EntityState.Modified, context.Entry(person).State);
        Assert.Empty(((ProviderTestDriver)(object)driver).Calls);
    }

    public sealed class Person
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = "";
    }
}
