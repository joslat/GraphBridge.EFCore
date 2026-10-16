using System.Globalization;

namespace GraphBridge.Benchmarks;

public sealed record BenchmarkOptions(int[] GraphSizes, int[] EdgeSizes, int[] WriteSizes, int Samples, int Warmups, int FetchSize,
    int Seed, string OutputDirectory)
{
    public static BenchmarkOptions Parse(string[] arguments)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var argument in arguments)
        {
            var pieces = argument.Split('=', 2);
            if (pieces.Length != 2 || !values.TryAdd(pieces[0], pieces[1]))
                throw new ArgumentException("Use unique --name=value options.");
        }
        string Value(string key, string fallback) => values.Remove(key, out var value) ? value : fallback;
        int Number(string key, int fallback, int minimum, int maximum)
        {
            var value = int.Parse(Value(key, fallback.ToString(CultureInfo.InvariantCulture)), CultureInfo.InvariantCulture);
            if (value < minimum || value > maximum) throw new ArgumentOutOfRangeException(key);
            return value;
        }
        int[] Sizes(string key, string fallback, int[] allowed)
        {
            var result = Value(key, fallback).Split(',').Select(x =>
                int.TryParse(x, NumberStyles.Integer, CultureInfo.InvariantCulture, out var size) ? size
                    : throw new ArgumentException("Invalid bounded integer size for " + key)).ToArray();
            if (result.Length == 0 || result.Length != result.Distinct().Count() || result.Any(x => !allowed.Contains(x)))
                throw new ArgumentException("Invalid or repeated bounded size for " + key);
            return result;
        }
        var options = new BenchmarkOptions(Sizes("--sizes", "1,100,10000,100000", [1, 100, 10000, 100000]),
            Sizes("--edge-sizes", "1,100,10000", [1, 100, 10000]),
            Sizes("--writes", "1,100,1000", [1, 100, 1000]), Number("--samples", 20, 2, 200),
            Number("--warmups", 3, 0, 20), Number("--fetch-size", 1000, 1, 10000),
            Number("--seed", 1729, 0, int.MaxValue),
            Path.GetFullPath(Value("--output", Path.Combine(".artifacts", "benchmarks", Guid.NewGuid().ToString("N")))));
        if (values.Count != 0) throw new ArgumentException("Unknown benchmark option: " + values.Keys.First());
        return options;
    }
}
