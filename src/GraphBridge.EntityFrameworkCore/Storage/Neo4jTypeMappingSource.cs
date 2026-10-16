using System.Globalization;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace GraphBridge.EntityFrameworkCore.Storage;

public sealed class Neo4jTypeMappingSource(TypeMappingSourceDependencies dependencies) : TypeMappingSource(dependencies)
{
    public static bool IsNativeScalar(Type type)
        => type == typeof(string) || type == typeof(int) || type == typeof(long)
            || type == typeof(bool) || type == typeof(double) || type == typeof(DateTimeOffset);

    public static bool IsSupportedModelScalar(Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        return IsNativeScalar(type) || type == typeof(Guid) || type.IsEnum;
    }

    protected override CoreTypeMapping? FindMapping(in TypeMappingInfo mappingInfo)
    {
        var type = mappingInfo.ClrType;
        if (type is null || mappingInfo.ElementTypeMapping is not null)
        {
            return null;
        }

        type = Nullable.GetUnderlyingType(type) ?? type;
        if (type == typeof(DateTimeOffset))
            return new Neo4jTypeMapping(type, new ValueComparer<DateTimeOffset>(
                (a, b) => a.EqualsExact(b), value => HashCode.Combine(value.Ticks, value.Offset)));
        if (IsNativeScalar(type))
        {
            return new Neo4jTypeMapping(type);
        }

        if (type == typeof(Guid))
        {
            return new Neo4jTypeMapping(type,
                new ValueConverter<Guid, string>(g => g.ToString("D"), s => Guid.ParseExact(s, "D")));
        }

        if (type.IsEnum)
        {
            return new Neo4jTypeMapping(type, (ValueConverter)Activator.CreateInstance(
                typeof(CheckedEnumConverter<>).MakeGenericType(type))!);
        }

        return null;
    }

    /// <summary>True for converters whose provider order equals the model order (the built-in enum converter).</summary>
    internal static bool PreservesOrder(ValueConverter converter)
        => converter.GetType() is { IsGenericType: true } type && type.GetGenericTypeDefinition() == typeof(CheckedEnumConverter<>);

    private sealed class CheckedEnumConverter<TEnum>() : ValueConverter<TEnum, long>(
        value => Convert.ToInt64(value, CultureInfo.InvariantCulture),
        value => FromInteger(value)) where TEnum : struct, Enum
    {
        private static TEnum FromInteger(long value)
            => (TEnum)Enum.ToObject(typeof(TEnum),
                Convert.ChangeType(value, Enum.GetUnderlyingType(typeof(TEnum)), CultureInfo.InvariantCulture));
    }
}
