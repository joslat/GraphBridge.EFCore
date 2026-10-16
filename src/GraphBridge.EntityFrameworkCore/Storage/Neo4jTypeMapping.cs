using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.EntityFrameworkCore.Storage.Json;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace GraphBridge.EntityFrameworkCore.Storage;

public sealed class Neo4jTypeMapping : CoreTypeMapping
{
    public Neo4jTypeMapping(Type clrType, ValueConverter? converter = null)
        : this(new CoreTypeMappingParameters(clrType, converter))
    {
    }

    internal Neo4jTypeMapping(Type clrType, ValueComparer comparer)
        : this(new CoreTypeMappingParameters(clrType, comparer: comparer)) { }

    private Neo4jTypeMapping(CoreTypeMappingParameters parameters) : base(parameters)
    {
    }

    protected override CoreTypeMapping Clone(CoreTypeMappingParameters parameters) => new Neo4jTypeMapping(parameters);

    public override CoreTypeMapping WithComposedConverter(
        ValueConverter? converter,
        ValueComparer? comparer = null,
        ValueComparer? keyComparer = null,
        CoreTypeMapping? elementMapping = null,
        JsonValueReaderWriter? jsonValueReaderWriter = null)
        => new Neo4jTypeMapping(Parameters.WithComposedConverter(
            converter, comparer, keyComparer, elementMapping, jsonValueReaderWriter));
}
