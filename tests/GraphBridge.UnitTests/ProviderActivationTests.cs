using System.Reflection;
using GraphBridge.EntityFrameworkCore.Infrastructure;
using GraphBridge.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Neo4j.Driver;

namespace GraphBridge.UnitTests;

public sealed class ProviderActivationTests
{
    [Fact]
    public void Context_recognizes_provider_and_resolves_required_services_without_driver_calls()
    {
        var driver = ProviderTestDriver.Create();
        using (var context = new ProviderModelContext(driver, b => b.Entity<Person>().ToGraphNode("Person")))
        {
            Assert.Equal("GraphBridge.EntityFrameworkCore", context.Database.ProviderName);
            Assert.NotNull(context.Model.FindEntityType(typeof(Person)));
            Assert.NotNull(context.GetService<IDatabase>());
            Assert.NotNull(context.GetService<IDatabaseCreator>());
            Assert.NotNull(context.GetService<IDbContextTransactionManager>());
            var binding = context.GetService<GraphBindingCompiler>().GetNode(context.Model.FindEntityType(typeof(Person))!);
            Assert.Equal("Person", binding.Label);
        }
        Assert.Empty(((ProviderTestDriver)(object)driver).Calls);
    }

    [Fact]
    public void Replacing_options_preserves_earlier_options_and_borrowed_driver_per_context()
    {
        var first = ProviderTestDriver.Create();
        var second = ProviderTestDriver.Create();
        var builder = new DbContextOptionsBuilder().UseNeo4j(first, "first");
        var original = builder.Options;
        builder.UseNeo4j(second, "second");
        Assert.Same(first, original.FindExtension<Neo4jDbContextOptionsExtension>()!.Driver);
        Assert.Equal("first", original.FindExtension<Neo4jDbContextOptionsExtension>()!.DatabaseName);
        Assert.Same(second, builder.Options.FindExtension<Neo4jDbContextOptionsExtension>()!.Driver);

        using var a = new ProviderModelContext(first, b => b.Entity<Person>().ToGraphNode("A"), "first");
        using var c = new ProviderModelContext(second, b => b.Entity<Person>().ToGraphNode("C"), "second");
        var aOptions = a.GetService<IDbContextOptions>().FindExtension<Neo4jDbContextOptionsExtension>()!;
        var cOptions = c.GetService<IDbContextOptions>().FindExtension<Neo4jDbContextOptionsExtension>()!;
        Assert.Same(first, aOptions.Driver);
        Assert.Same(second, cOptions.Driver);
        Assert.Equal("first", aOptions.DatabaseName);
        Assert.Equal("second", cOptions.DatabaseName);
        Assert.Same(a.GetService<ITypeMappingSource>(), c.GetService<ITypeMappingSource>());
    }

    [Fact]
    public void Options_validate_arguments_and_do_not_log_database_or_driver_details()
    {
        var driver = ProviderTestDriver.Create();
        Assert.Throws<ArgumentNullException>(() => new DbContextOptionsBuilder().UseNeo4j(null!));
        Assert.Throws<ArgumentException>(() => new DbContextOptionsBuilder().UseNeo4j(driver, " "));
        var extension = new Neo4jDbContextOptionsExtension(driver, "sensitive_database_name");
        Assert.DoesNotContain("sensitive", extension.Info.LogFragment);
        var debug = new Dictionary<string, string>();
        extension.Info.PopulateDebugInfo(debug);
        Assert.DoesNotContain("sensitive", string.Join(",", debug.Values));
        Assert.True(extension.Info.ShouldUseSameServiceProvider(new Neo4jDbContextOptionsExtension(driver, "other").Info));
    }

    [Fact]
    public void Provider_registration_is_idempotent()
    {
        var services = new ServiceCollection();
        services.AddEntityFrameworkNeo4j();
        var count = services.Count;
        services.AddEntityFrameworkNeo4j();
        Assert.Equal(count, services.Count);
    }

    public sealed class Person
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = "";
        public int Age { get; set; }
    }
}

public class ProviderTestDriver : DispatchProxy
{
    public List<string> Calls { get; } = [];
    public static IDriver Create() => Create<IDriver, ProviderTestDriver>();

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        Calls.Add(targetMethod!.Name);
        throw new InvalidOperationException($"Unexpected driver call during a unit test: {targetMethod.Name}");
    }
}

public sealed class ProviderModelContext : DbContext
{
    public ProviderModelContext(IDriver driver, Action<ModelBuilder> mapping, string database = "neo4j")
        : base(new DbContextOptionsBuilder<ProviderModelContext>()
            .UseNeo4j(driver, database)
            .ReplaceService<IModelCacheKeyFactory, ProviderTestModelCacheKeyFactory>().Options)
        => Mapping = mapping;

    public Action<ModelBuilder> Mapping { get; }
    protected override void OnModelCreating(ModelBuilder modelBuilder) => Mapping(modelBuilder);
}

public sealed class ProviderTestModelCacheKeyFactory : IModelCacheKeyFactory
{
    public object Create(DbContext context, bool designTime)
        => (context.GetType(), ((ProviderModelContext)context).Mapping, designTime);
}
