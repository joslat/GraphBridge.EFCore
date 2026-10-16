using Microsoft.EntityFrameworkCore;

namespace GraphBridge.IntegrationTests;

public sealed partial class ProviderOptionalReferenceTests
{
    [Neo4jIntegrationFact]
    public async Task Bulk_optional_reference_handles_absent_and_present_parents_without_detach()
    {
        var run = Guid.NewGuid().ToString("N");
        try
        {
            var (parent, other, alone, linked) = await Seed(run);
            await using var context = Context();
            Assert.Equal(3, await context.Set<Child>().Where(c => c.Run == run)
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.Title, "bulk")));
            var children = await context.Set<Child>().AsNoTracking().Where(c => c.Run == run).ToListAsync();
            Assert.All(children, c => { Assert.Equal("bulk", c.Title); Assert.Equal(2, c.Version); });
            Assert.Null(children.Single(c => c.Id == alone.Id).ParentId);
            Assert.Equal(parent.Id, children.Single(c => c.Id == linked.Id).ParentId);
            Assert.Equal(1, await context.Set<Child>().Where(c => c.Run == run && c.ParentId == null).ExecuteDeleteAsync());
            Assert.Equal(1, await context.Set<Child>().Where(c => c.Id == linked.Id).ExecuteDeleteAsync());
            Assert.Single(await context.Set<Child>().AsNoTracking().Where(c => c.Run == run).ToListAsync());
            Assert.Equal(2, await context.Set<Parent>().CountAsync(p => p.Run == run));
        }
        finally { await Cleanup(run); }
    }
}
