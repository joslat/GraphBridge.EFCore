using System.Collections;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage;

namespace GraphBridge.EntityFrameworkCore.Storage;

/// <summary>Embedding vectors mapped with <c>IsGraphVector</c> (ADR 0054): stored as a list of floats, checked before every write.
/// Messages name the entity type and property, never the values.</summary>
internal static class Neo4jVectorValues
{
    internal static CoreTypeMapping Mapping(Type modelType) => modelType == typeof(float[])
        ? new Neo4jTypeMapping(typeof(float[]), new ValueComparer<float[]>(
            (a, b) => Equal(a, b), value => Hash(value), value => Snapshot(value)))
        : new Neo4jTypeMapping(typeof(ReadOnlyMemory<float>), new ValueComparer<ReadOnlyMemory<float>>(
            (a, b) => Equal(a, b), value => Hash(value), value => Snapshot(value)));

    internal static object ToStore(object value, int dimensions, string describe)
    {
        var span = value switch
        {
            float[] array => array.AsSpan(),
            ReadOnlyMemory<float> memory => memory.Span,
            _ => throw new InvalidOperationException($"GraphBridge vector property {describe} holds a {value.GetType().Name}; it maps float[] or ReadOnlyMemory<float>.")
        };
        if (span.Length != dimensions)
            throw new InvalidOperationException($"GraphBridge vector property {describe} has {span.Length} values; it is mapped with {dimensions} dimensions. Nothing was sent.");
        var zero = true;
        var stored = new double[span.Length];
        for (var i = 0; i < span.Length; i++)
        {
            if (!float.IsFinite(span[i]))
                throw new InvalidOperationException($"GraphBridge vector property {describe} contains a NaN or infinite value. Nothing was sent.");
            zero &= span[i] == 0;
            stored[i] = span[i];
        }
        if (zero)
            throw new InvalidOperationException($"GraphBridge vector property {describe} is all zeros, which a cosine index can't score and no embedding produces. Nothing was sent.");
        return stored;
    }

    internal static object FromStore(object value, int dimensions, Type modelType, string describe)
    {
        if (value is Neo4j.Driver.IVector)
            throw new InvalidOperationException($"GraphBridge vector property {describe} holds a native VECTOR value; GraphBridge stores and reads vectors as lists of floats.");
        if (value is not IList list || value is string)
            throw new InvalidOperationException($"GraphBridge vector property {describe} holds a {value.GetType().Name}, not a list of numbers.");
        if (list.Count != dimensions)
            throw new InvalidOperationException($"GraphBridge vector property {describe} holds {list.Count} values; it is mapped with {dimensions} dimensions.");
        var result = new float[dimensions];
        for (var i = 0; i < dimensions; i++)
        {
            result[i] = list[i] switch
            {
                double number => (float)number,
                long integer => integer,
                int integer => integer,
                _ => throw new InvalidOperationException($"GraphBridge vector property {describe} holds a list with a non-numeric element.")
            };
            if (!float.IsFinite(result[i]))
                throw new InvalidOperationException($"GraphBridge vector property {describe} holds a value that is not a finite float32.");
        }
        // Boxed separately: a conditional would convert the array to ReadOnlyMemory<float> implicitly.
        return modelType == typeof(float[]) ? (object)result : new ReadOnlyMemory<float>(result);
    }

    // Change tracking compares contents bit for bit, so an in-place change to the array is a change.
    internal static bool Equal(float[]? a, float[]? b)
        => ReferenceEquals(a, b) || a is not null && b is not null && Equal(new ReadOnlyMemory<float>(a), new ReadOnlyMemory<float>(b));

    internal static bool Equal(ReadOnlyMemory<float> a, ReadOnlyMemory<float> b)
    {
        if (a.Length != b.Length) return false;
        var x = a.Span;
        var y = b.Span;
        for (var i = 0; i < x.Length; i++)
            if (BitConverter.SingleToInt32Bits(x[i]) != BitConverter.SingleToInt32Bits(y[i])) return false;
        return true;
    }

    internal static int Hash(float[]? value) => value is null ? 0 : Hash(new ReadOnlyMemory<float>(value));

    internal static int Hash(ReadOnlyMemory<float> value)
    {
        var hash = new HashCode();
        hash.Add(value.Length);
        foreach (var element in value.Span[..Math.Min(value.Length, 8)]) hash.Add(BitConverter.SingleToInt32Bits(element));
        return hash.ToHashCode();
    }

    internal static float[] Snapshot(float[] value) => (float[])value.Clone();

    internal static ReadOnlyMemory<float> Snapshot(ReadOnlyMemory<float> value) => value.ToArray();
}
