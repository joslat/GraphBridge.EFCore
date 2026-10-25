using GraphBridge.EntityFrameworkCore.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Neo4j.Driver;

namespace GraphBridge.EntityFrameworkCore.Metadata;

internal sealed class GraphPropertyBinding
{
    private readonly ValueConverter? _converter;
    private readonly Type _storeType;

    internal GraphPropertyBinding(IProperty property, string name)
    {
        Property = property;
        Name = name;
        var mapping = property.GetTypeMapping();
        _converter = mapping.Converter;
        _storeType = _converter?.ProviderClrType ?? mapping.ClrType;
        VectorDimensions = property.FindAnnotation(GraphAnnotationNames.VectorDimensions)?.Value as int?;
    }

    public IProperty Property { get; }
    public string Name { get; }
    internal bool UsesNativeInstantSemantics => _converter is null && _storeType == typeof(DateTimeOffset);
    /// <summary>Set for a property mapped with <c>IsGraphVector</c>; its values are lists of floats of this length.</summary>
    internal int? VectorDimensions { get; }

    public object? ToStore(object? value)
    {
        if (value is null)
            return ValidateNull();
        if (VectorDimensions is { } dimensions)
            return Neo4jVectorValues.ToStore(value, dimensions, Describe);
        var modelValue = value;
        value = _converter is null ? value : _converter.ConvertToProvider(value);
        if (value is null)
            return ValidateNull();
        if (_storeType == typeof(int))
            value = (long)(int)value;
        else if (!_storeType.IsInstanceOfType(value))
            throw WrongType(value);
        if ((Property.IsPrimaryKey() || Property.IsForeignKey())
            && !Property.GetKeyValueComparer().Equals(modelValue, FromStore(value)))
            throw new InvalidOperationException($"GraphBridge identity property '{Property.Name}' must round-trip through its converter without changing EF identity.");
        return _storeType == typeof(DateTimeOffset) ? new ZonedDateTime((DateTimeOffset)value) : value;
    }

    public object? FromStore(object? value)
    {
        if (value is null)
            return ValidateNull();
        if (VectorDimensions is { } dimensions)
            return Neo4jVectorValues.FromStore(value, dimensions, _storeType, Describe);
        if (_storeType == typeof(DateTimeOffset))
        {
            if (value is not ZonedDateTime temporal) throw WrongType(value);
            // The pinned driver rejects sub-tick precision, invalid offsets and BCL overflow.
            value = temporal.ToDateTimeOffset();
        }
        if (_storeType == typeof(int) && value is long integer)
            value = checked((int)integer);
        else if (_storeType == typeof(long) && value is int smallInteger)
            value = (long)smallInteger;
        if (!_storeType.IsInstanceOfType(value))
            throw WrongType(value);
        var modelValue = _converter is null ? value : _converter.ConvertFromProvider(value);
        return modelValue is null ? ValidateNull() : modelValue;
    }

    private string Describe => $"'{Property.DeclaringType.DisplayName()}.{Property.Name}'";

    private object? ValidateNull()
        => Property.IsNullable ? null
            : throw new InvalidOperationException($"GraphBridge required property '{Property.DeclaringType.Name}.{Property.Name}' is null or missing.");

    private InvalidOperationException WrongType(object value)
        => new($"GraphBridge property '{Property.DeclaringType.Name}.{Property.Name}' expects store type '{_storeType.Name}', received '{value.GetType().Name}'.");
}
