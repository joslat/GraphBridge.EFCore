using System.Globalization;
using System.Text;

namespace GraphBridge.Tooling.Generation;

internal static class GraphCSharpNames
{
    private static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
    {
        "abstract", "as", "base", "bool", "break", "byte", "case", "catch", "char", "checked", "class", "const",
        "continue", "decimal", "default", "delegate", "do", "double", "else", "enum", "event", "explicit", "extern",
        "false", "finally", "fixed", "float", "for", "foreach", "goto", "if", "implicit", "in", "int", "interface",
        "internal", "is", "lock", "long", "namespace", "new", "null", "object", "operator", "out", "override", "params",
        "private", "protected", "public", "readonly", "ref", "return", "sbyte", "sealed", "short", "sizeof", "stackalloc",
        "static", "string", "struct", "switch", "this", "throw", "true", "try", "typeof", "uint", "ulong", "unchecked",
        "unsafe", "ushort", "using", "virtual", "void", "volatile", "while", "record", "required", "file", "scoped",
        "global", "var", "dynamic", "nint", "nuint", "not", "and", "or", "with", "init", "value", "field",
        "__arglist", "__makeref", "__reftype", "__refvalue"
    };

    internal static string Clr(string value)
    {
        var result = new StringBuilder();
        foreach (var character in value)
        {
            if (character is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or '_' or >= '0' and <= '9')
                result.Append(character);
            else result.Append("_u").Append(((int)character).ToString("X4", CultureInfo.InvariantCulture)).Append('_');
        }
        if (result.Length == 0) return "_";
        if (result[0] is >= '0' and <= '9') result.Insert(0, '_');
        return result.ToString();
    }

    internal static string Identifier(string value)
    {
        var name = Clr(value);
        return Keywords.Contains(name) ? "@" + name : name;
    }

    internal static string Namespace(string value) => string.Join('.', value.Split('.').Select(Identifier));
    internal static string ReflectionNamespace(string value) => string.Join('.', value.Split('.').Select(Clr));

    internal static string Literal(string value)
    {
        var result = new StringBuilder("\"");
        foreach (var character in value)
            switch (character)
            {
                case '\\': result.Append("\\\\"); break;
                case '"': result.Append("\\\""); break;
                case '\n': result.Append("\\n"); break;
                case '\r': result.Append("\\r"); break;
                case '\t': result.Append("\\t"); break;
                default:
                    if (character is < ' ' or > '~')
                        result.Append("\\u").Append(((int)character).ToString("X4", CultureInfo.InvariantCulture));
                    else result.Append(character);
                    break;
            }
        return result.Append('"').ToString();
    }

    internal static bool IsStoreIdentifier(string value) => value.Length > 0
        && (value[0] is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or '_')
        && value.All(character => character is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or '_' or >= '0' and <= '9');
}
