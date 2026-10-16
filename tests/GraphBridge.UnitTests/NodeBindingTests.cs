using System.Globalization;
using GraphBridge.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;

namespace GraphBridge.UnitTests;

public sealed class NodeBindingTests
{
    [Fact]
    public void Binding_uses_final_model_names_keys_property_ordinals_and_is_immutable()
    {
        Action<ModelBuilder> configure = b =>
        {
            var entity = b.Entity<Scalars>().ToGraphNode("ScalarNode");
            entity.Property(p => p.Id).HasGraphPropertyName("id");
            entity.Property(p => p.Age).HasGraphPropertyName("age");
            entity.Property<string>("Shadow");
        };
        using var context = Context(configure);
        var binding = Binding<Scalars>(context);
        Assert.Same(context.Model.FindEntityType(typeof(Scalars)), binding.EntityType);
        Assert.Equal("ScalarNode", binding.Label);
        Assert.Equal("Id", Assert.Single(binding.KeyProperties).Name);
        Assert.Equal("age", Property(binding, "Age").Name);
        Assert.Equal("Name", Property(binding, "Name").Name);
        Assert.Contains(binding.Properties, p => p.Property.IsShadowProperty() && p.Name == "Shadow");
        Assert.Equal(Enumerable.Range(0, binding.Properties.Count), binding.Properties.Select(p => p.Property.GetIndex()));
        Assert.Throws<NotSupportedException>(() => ((IList<GraphPropertyBinding>)binding.Properties).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<IProperty>)binding.KeyProperties).Clear());
        using var second = new DbContext(new DbContextOptionsBuilder().UseNeo4j(ProviderTestDriver.Create()).UseModel(context.Model).Options);
        Assert.Same(binding, second.GetService<GraphBindingCompiler>().GetNode(second.Model.FindEntityType(typeof(Scalars))!));
    }

    [Fact]
    public void Scalar_conversions_preserve_nullable_enum_guid_and_checked_integer_semantics()
    {
        using var context = Context(b => b.Entity<Scalars>().ToGraphNode("Scalars"));
        var binding = Binding<Scalars>(context);
        var id = Guid.Parse("abcae840-e29b-41d4-a716-446655440000");
        Assert.Equal(id.ToString("D"), Property(binding, "Id").ToStore(id));
        Assert.Equal(id, Property(binding, "Id").FromStore(id.ToString("D")));
        Assert.Equal(23L, Property(binding, "Age").ToStore(23));
        Assert.Equal(23, Property(binding, "Age").FromStore(23L));
        Assert.Throws<OverflowException>(() => Property(binding, "Age").FromStore((long)int.MaxValue + 1));
        Assert.Equal(42L, Property(binding, "Count").FromStore(42L));
        Assert.Equal(true, Property(binding, "Active").FromStore(true));
        Assert.Equal(1.25, Property(binding, "Score").FromStore(1.25));
        Assert.Equal("Ada", Property(binding, "Name").FromStore("Ada"));
        Assert.Equal(2L, Property(binding, "Status").ToStore(Status.Done));
        Assert.Equal(Status.Done, Property(binding, "Status").FromStore(2L));
        Assert.Null(Property(binding, "OptionalStatus").FromStore(null));
        Assert.Null(Property(binding, "OptionalAge").ToStore(null));
        Assert.Equal(Status.Done, Property(binding, "OptionalStatus").FromStore(2L));
        Assert.Throws<InvalidOperationException>(() => Property(binding, "Age").FromStore(null));
        Assert.Throws<InvalidOperationException>(() => Property(binding, "Age").FromStore("23"));
        Assert.Throws<FormatException>(() => Property(binding, "Id").FromStore("not-a-guid"));
    }

    [Fact]
    public void Enum_conversion_checks_both_signed_store_range_and_underlying_enum_range()
    {
        using var context = Context(b => b.Entity<WideEnumEntity>().ToGraphNode("Wide"));
        var binding = Binding<WideEnumEntity>(context);
        Assert.Throws<OverflowException>(() => Property(binding, "Wide").ToStore(WideStatus.Overflow));
        Assert.Throws<OverflowException>(() => Property(binding, "Byte").FromStore(256L));
        Assert.Throws<OverflowException>(() => Property(binding, "Wide").FromStore(-1L));
    }

    [Fact]
    public void Explicit_decimal_and_date_converters_run_in_both_directions()
    {
        using var context = Context(b =>
        {
            var entity = b.Entity<ConvertedEntity>().ToGraphNode("Converted");
            entity.Property(p => p.Price).HasConversion(
                value => value.ToString(CultureInfo.InvariantCulture),
                value => decimal.Parse(value, CultureInfo.InvariantCulture));
            entity.Property(p => p.Timestamp).HasConversion(value => value.Ticks, value => new DateTime(value));
        });
        var binding = Binding<ConvertedEntity>(context);
        Assert.Equal("123.45", Property(binding, "Price").ToStore(123.45m));
        Assert.Equal(123.45m, Property(binding, "Price").FromStore("123.45"));
        var timestamp = new DateTime(2026, 10, 11);
        Assert.Equal(timestamp.Ticks, Property(binding, "Timestamp").ToStore(timestamp));
        Assert.Equal(timestamp, Property(binding, "Timestamp").FromStore(timestamp.Ticks));
    }

    [Fact]
    public void Explicit_provider_type_conversion_is_honored()
    {
        using var context = Context(b =>
        {
            var entity = b.Entity<ConvertedEntity>().ToGraphNode("Converted");
            entity.Property(p => p.Price).HasConversion<string>();
            entity.Property(p => p.Timestamp).HasConversion<long>();
        });
        var binding = Binding<ConvertedEntity>(context);
        Assert.Equal(123.45m, Property(binding, "Price").FromStore(Property(binding, "Price").ToStore(123.45m)));
    }

    [Fact]
    public void Explicit_converter_null_results_follow_property_nullability()
    {
        using var context = Context(b =>
        {
            var entity = b.Entity<NullConvertedEntity>().ToGraphNode("NullConverted");
            entity.Property(p => p.Optional).HasConversion(value => value == "empty" ? null : value,
                value => value == "empty" ? null : value);
            entity.Property(p => p.Required).HasConversion(value => value == "empty" ? null! : value,
                value => value == "empty" ? null! : value);
        });
        var binding = Binding<NullConvertedEntity>(context);
        Assert.Null(Property(binding, "Optional").ToStore("empty"));
        Assert.Null(Property(binding, "Optional").FromStore("empty"));
        Assert.Throws<InvalidOperationException>(() => Property(binding, "Required").ToStore("empty"));
        Assert.Throws<InvalidOperationException>(() => Property(binding, "Required").FromStore("empty"));
    }

    [Theory]
    [InlineData("bad`label")]
    [InlineData("x) MATCH (n)")]
    [InlineData("9bad")]
    [InlineData("é")]
    public void Unsafe_identifiers_are_rejected_when_configured(string name)
    {
        var model = new ModelBuilder();
        Assert.Throws<ArgumentException>(() => model.Entity<Scalars>().ToGraphNode(name));
        Assert.Throws<ArgumentException>(() => model.Entity<Scalars>().Property(p => p.Name).HasGraphPropertyName(name));
    }

    [Fact]
    public void Invalid_raw_annotations_cannot_bypass_identifier_validation()
    {
        using var context = Context(b => b.Entity<Scalars>().HasAnnotation(GraphAnnotationNames.NodeLabel, "bad`label"));
        Assert.Throws<ArgumentException>(() => context.Model);
    }

    [Fact]
    public void Missing_mapping_keyless_duplicate_label_and_duplicate_property_fail_before_transport()
    {
        ExpectInvalid(b => b.Entity<Scalars>(), "ToGraphNode");
        ExpectInvalid(b => b.Entity<Scalars>().ToGraphNode("Scalar").HasNoKey(), "primary key");
        ExpectInvalid(b =>
        {
            b.Entity<Scalars>().ToGraphNode("Same");
            b.Entity<WideEnumEntity>().ToGraphNode("Same");
        }, "already mapped");
        ExpectInvalid(b =>
        {
            var entity = b.Entity<Scalars>().ToGraphNode("Scalar");
            entity.Property(p => p.Name).HasGraphPropertyName("Id");
        }, "more than once");
    }

    [Fact]
    public void Unsupported_decimal_date_inheritance_and_complex_models_fail_before_transport()
    {
        ExpectInvalid(b => b.Entity<ConvertedEntity>().ToGraphNode("Converted"), "HasConversion");
        ExpectInvalid(b =>
        {
            b.Entity<BaseEntity>().ToGraphNode("Base");
            b.Entity<DerivedEntity>().ToGraphNode("Derived");
        }, "inheritance");
        ExpectInvalid(b =>
        {
            var entity = b.Entity<ComplexEntity>().ToGraphNode("Complex");
            entity.ComplexProperty(p => p.Address);
        }, "complex properties");
    }

    [Fact]
    public void Relationships_and_owned_models_are_rejected_before_transport()
    {
        ExpectInvalid(b =>
        {
            b.Entity<Owner>().ToGraphNode("Owner").HasOne(p => p.Child).WithMany();
            b.Entity<Child>().ToGraphNode("Child");
        }, "relationship storage");
        ExpectInvalid(b => b.Entity<ComplexEntity>().ToGraphNode("Owner").OwnsOne(p => p.Address), "unsupported");
    }

    private static ProviderModelContext Context(Action<ModelBuilder> configure) => new(ProviderTestDriver.Create(), configure);
    private static NodeBinding Binding<T>(ProviderModelContext context)
        => context.GetService<GraphBindingCompiler>().GetNode(context.Model.FindEntityType(typeof(T))!);
    private static GraphPropertyBinding Property(NodeBinding binding, string name)
        => binding.Properties.Single(p => p.Property.Name == name);
    private static void ExpectInvalid(Action<ModelBuilder> configure, string expected)
    {
        using var context = Context(configure);
        Assert.Contains(expected, Assert.Throws<InvalidOperationException>(() => context.Model).Message);
    }

    public enum Status { Started = 1, Done = 2 }
    public enum WideStatus : ulong { Overflow = ulong.MaxValue }
    public enum ByteStatus : byte { Zero }
    public sealed class Scalars
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = "";
        public int Age { get; set; }
        public int? OptionalAge { get; set; }
        public long Count { get; set; }
        public bool Active { get; set; }
        public double Score { get; set; }
        public Status Status { get; set; }
        public Status? OptionalStatus { get; set; }
    }
    public sealed class WideEnumEntity
    {
        public Guid Id { get; set; }
        public WideStatus Wide { get; set; }
        public ByteStatus Byte { get; set; }
    }
    public sealed class ConvertedEntity
    {
        public Guid Id { get; set; }
        public decimal Price { get; set; }
        public DateTime Timestamp { get; set; }
    }
    public sealed class NullConvertedEntity
    {
        public Guid Id { get; set; }
        public string? Optional { get; set; }
        public string Required { get; set; } = "";
    }
    public class BaseEntity { public Guid Id { get; set; } }
    public sealed class DerivedEntity : BaseEntity { public string Name { get; set; } = ""; }
    public sealed class Address { public string Street { get; set; } = ""; }
    public sealed class ComplexEntity { public Guid Id { get; set; } public Address Address { get; set; } = new(); }
    public sealed class Owner { public Guid Id { get; set; } public Child Child { get; set; } = new(); }
    public sealed class Child { public Guid Id { get; set; } }
}
