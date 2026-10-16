using System.Globalization;
using GraphBridge.IntegrationTests;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Query.Translations;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Microsoft.EntityFrameworkCore.TestModels.BasicTypesModel;
using Microsoft.EntityFrameworkCore.TestUtilities;

namespace GraphBridge.SpecificationTests;

public sealed class BasicTypesQueryNeo4jFixture : BasicTypesQueryFixtureBase
{
    private readonly DisposableNeo4jFixture _database = new();
    private readonly ITestStoreFactory _factory;
    private bool _connected;
    private bool _initializationStarted;

    public BasicTypesQueryNeo4jFixture() => _factory = new Neo4jSpecificationTestStoreFactory(_database);

    protected override ITestStoreFactory TestStoreFactory => _factory;
    protected override string StoreName => "GraphBridgeBasicTypesSpecifications";
    protected override bool RecreateStore => true;
    protected override bool UsePooling => false;
    protected override bool ShouldLogCategory(string category) => category == DbLoggerCategory.Database.Command.Name;

    public override async Task InitializeAsync()
    {
        if (!RequiresDisposableNeo4jAttribute.Enabled)
            return;

        // Reuse all existing localhost, database and marker checks. This opens a driver, not another server.
        await _database.InitializeAsync();
        _connected = true;
        _initializationStarted = true;
        await base.InitializeAsync();
    }

    public override async Task DisposeAsync()
    {
        try
        {
            if (_initializationStarted)
                await base.DisposeAsync();
        }
        finally
        {
            if (_connected)
                await _database.DisposeAsync();
        }
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder, DbContext context)
    {
        base.OnModelCreating(modelBuilder, context);
        modelBuilder.Entity<BasicTypesEntity>().ToGraphNode(Neo4jSpecificationTestStore.BasicLabel);
        modelBuilder.Entity<NullableBasicTypesEntity>().ToGraphNode(Neo4jSpecificationTestStore.NullableLabel);

        foreach (var entityType in new[] { typeof(BasicTypesEntity), typeof(NullableBasicTypesEntity) })
        {
            foreach (var property in entityType.GetProperties())
            {
                // Declare every upstream scalar explicitly: unsupported convention discovery must
                // not silently omit a field before we supply its whole-value conversion.
                var propertyBuilder = modelBuilder.Entity(entityType).Property(property.PropertyType, property.Name);
                var type = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
                var converter = Converter(type);
                if (converter is null)
                    continue;
                propertyBuilder.HasConversion(converter);
                if (type == typeof(byte[]))
                {
                    propertyBuilder.Metadata.SetValueComparer(new ValueComparer<byte[]?>(
                        (left, right) => left == right || (left != null && right != null && left.SequenceEqual(right)),
                        value => value == null ? 0 : value.Aggregate(0, (hash, item) => HashCode.Combine(hash, item)),
                        value => value == null ? null : value.ToArray()));
                }
            }
        }
    }

    // Explicit fixture representations preserve the entire upstream dataset; they do not claim
    // native Neo4j temporal/decimal/array translation. EF passes null through these converters.
    private static ValueConverter? Converter(Type type)
    {
        if (type == typeof(byte))
            return new ValueConverter<byte, long>(value => value, value => checked((byte)value));
        if (type == typeof(short))
            return new ValueConverter<short, long>(value => value, value => checked((short)value));
        if (type == typeof(float))
            return new ValueConverter<float, double>(value => value, value => (float)value);
        if (type == typeof(decimal))
            return new NumberToStringConverter<decimal>();
        if (type == typeof(DateTime))
            return new DateTimeToBinaryConverter();
        if (type == typeof(DateOnly))
            return new ValueConverter<DateOnly, int>(value => value.DayNumber, value => DateOnly.FromDayNumber(value));
        if (type == typeof(TimeOnly))
            return new TimeOnlyToTicksConverter();
        if (type == typeof(DateTimeOffset))
            return new ValueConverter<DateTimeOffset, string>(
                value => value.ToString("O", CultureInfo.InvariantCulture),
                value => DateTimeOffset.ParseExact(value, "O", CultureInfo.InvariantCulture, DateTimeStyles.None));
        if (type == typeof(TimeSpan))
            return new TimeSpanToTicksConverter();
        if (type == typeof(byte[]))
            return new BytesToStringConverter();
        return null;
    }
}
