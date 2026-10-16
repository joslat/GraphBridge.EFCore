namespace GraphBridge.EntityFrameworkCore.Metadata;

internal static class GraphIdentifier
{
    public static string Validate(string name, string argumentName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name, argumentName);
        if (!IsInitial(name[0]) || name.Any(c => !IsInitial(c) && c is not (>= '0' and <= '9')))
        {
            throw new ArgumentException(
                "Graph identifiers must match [A-Za-z_][A-Za-z0-9_]*.", argumentName);
        }

        return name;
    }

    private static bool IsInitial(char c) => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or '_';
}
