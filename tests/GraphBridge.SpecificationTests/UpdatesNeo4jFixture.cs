using GraphBridge.IntegrationTests;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Microsoft.EntityFrameworkCore.TestModels.UpdatesModel;
using Microsoft.EntityFrameworkCore.TestUtilities;
using Microsoft.EntityFrameworkCore.Update;

namespace GraphBridge.SpecificationTests;

/// <summary>Scalar update slice with original upstream seed and concurrency annotations.</summary>
public sealed class UpdatesNeo4jFixture : UpdatesTestBase<UpdatesNeo4jFixture>.UpdatesFixtureBase
{
    private readonly DisposableNeo4jFixture _database = new();
    private readonly ITestStoreFactory _factory;
    private bool _connected;
    private bool _initializationStarted;
    public UpdatesNeo4jFixture() => _factory = new Neo4jSpecificationTestStoreFactory(_database, SpecificationStoreProfile.Updates);
    protected override ITestStoreFactory TestStoreFactory => _factory;
    protected override string StoreName => "GraphBridgeUpdateSpecifications";
    protected override bool RecreateStore => true;
    protected override bool UsePooling => false;
    protected override bool ShouldLogCategory(string category) => category == DbLoggerCategory.Database.Command.Name;
    public override DbContextOptionsBuilder AddOptions(DbContextOptionsBuilder builder)
        => base.AddOptions(builder).ConfigureWarnings(w => w.Log(
            CoreEventId.MappedEntityTypeIgnoredWarning, CoreEventId.MappedNavigationIgnoredWarning));

    public override async Task InitializeAsync()
    {
        if (!RequiresDisposableNeo4jAttribute.Enabled) return;
        await _database.InitializeAsync();
        _connected = true;
        _initializationStarted = true;
        await base.InitializeAsync();
    }

    public override async Task DisposeAsync()
    {
        try { if (_initializationStarted) await base.DisposeAsync(); }
        finally { if (_connected) await _database.DisposeAsync(); }
    }

    public override async Task ReseedAsync()
    {
        await using var context = CreateContext();
        await TestStore.CleanAsync(context);
        await CleanAsync(context);
        await SeedAsync(context); // The original UpdatesContext seed, unchanged.
    }

    internal void ConfigureModelForVerification(ModelBuilder builder, DbContext context) => OnModelCreating(builder, context);

    protected override void OnModelCreating(ModelBuilder builder, DbContext context)
    {
        base.OnModelCreating(builder, context);
        var included = new[] { typeof(Product), typeof(Category), typeof(ProductWithBytes), typeof(AFewBytes) };
        // Flatten the shared CLR base into independent node roots for this named scalar slice.
        builder.Entity<Product>().HasBaseType((Type?)null);
        builder.Entity<ProductWithBytes>().HasBaseType((Type?)null);
        foreach (var type in builder.Model.GetEntityTypes().Select(t => t.ClrType).Distinct().Where(t => !included.Contains(t)).ToArray())
            builder.Ignore(type);

        var product = builder.Entity<Product>().ToGraphNode("GraphBridgeSpecUpdateProduct");
        product.Ignore(p => p.DefaultCategory);
        product.Ignore(p => p.ProductCategories);
        product.Property(p => p.Id).ValueGeneratedNever();
        product.Property(p => p.Price).HasConversion(new NumberToStringConverter<decimal>());

        var category = builder.Entity<Category>().ToGraphNode("GraphBridgeSpecUpdateCategory");
        category.Ignore(c => c.ProductCategories);
        // The seed includes one category. Its default ID is a stable fixture key, not store-generated.
        category.Property(c => c.Id).ValueGeneratedNever();

        var bytesProduct = builder.Entity<ProductWithBytes>().ToGraphNode("GraphBridgeSpecUpdateProductBytes");
        bytesProduct.Ignore(p => p.ProductCategories);
        bytesProduct.Property(p => p.Id).ValueGeneratedNever();
        bytesProduct.Property(p => p.Bytes).HasConversion((ValueConverter)new BytesToStringConverter());

        var bytes = builder.Entity<AFewBytes>().ToGraphNode("GraphBridgeSpecUpdateBytes");
        bytes.Property(p => p.Id).ValueGeneratedNever();
        bytes.Property(p => p.Bytes).HasConversion((ValueConverter)new BytesToStringConverter());
        // Keep EF's default shallow byte[] snapshots (equality is structural in EF 10.0.12).
        // The upstream mutation test marks
        // one array property modified explicitly and expects the other in-place edit not to save.
        // Declare it explicitly because EF requires a comparer for converted collection values.
        foreach (var property in new[] { bytesProduct.Property(p => p.Bytes).Metadata, bytes.Property(p => p.Bytes).Metadata })
            property.SetValueComparer(ValueComparer.CreateDefault(typeof(byte[]), favorStructuralComparisons: false));
    }
}
