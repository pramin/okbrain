using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace OKBrain.Core;

internal static class MetadataAccess
{
    public static YamlNode? Get(YamlMappingNode? mapping, string key) =>
        mapping != null && mapping.Children.TryGetValue(new YamlScalarNode(key), out var value) ? value : null;

    public static string? StringValue(YamlMappingNode? mapping, string key) =>
        IsString(Get(mapping, key), out var value) ? value : null;

    public static bool IsString(YamlNode? node, out string value)
    {
        value = "";
        if (node is not YamlScalarNode scalar || string.IsNullOrWhiteSpace(scalar.Value)) return false;
        var tag = scalar.Tag.ToString();
        if (tag == "tag:yaml.org,2002:str") { value = scalar.Value; return true; }
        if (!scalar.Tag.IsEmpty && tag != "?") return false;
        if (scalar.Style is ScalarStyle.Plain or ScalarStyle.Any)
        {
            var text = scalar.Value;
            if (text is "~" || text.Equals("null", StringComparison.OrdinalIgnoreCase) ||
                bool.TryParse(text, out _) || Regex.IsMatch(text, @"^[-+]?(?:[0-9][0-9_]*(?:\.[0-9_]*)?(?:[eE][-+]?[0-9]+)?|\.[0-9_]+(?:[eE][-+]?[0-9]+)?|0[xX][0-9a-fA-F_]+|0[oO][0-7_]+|\.(?:inf|Inf|INF|nan|NaN|NAN))$")) return false;
        }
        value = scalar.Value;
        return true;
    }

    public static bool TryInteger(YamlNode? node, out BigInteger result)
    {
        result = default;
        if (node is not YamlScalarNode scalar || scalar.Value == null || !HasScalarType(scalar, "int")) return false;
        var value = scalar.Value;
        if (!Regex.IsMatch(value, @"^[-+]?(?:[0-9](?:[0-9_]*[0-9])?|0[xX][0-9a-fA-F](?:[0-9a-fA-F_]*[0-9a-fA-F])?|0[oO][0-7](?:[0-7_]*[0-7])?)$")) return false;
        value = value.Replace("_", "", StringComparison.Ordinal);
        var negative = value.StartsWith('-');
        value = value.TrimStart('-', '+');
        if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            if (!BigInteger.TryParse("0" + value[2..], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out result)) return false;
        }
        else if (value.StartsWith("0o", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var digit in value[2..]) result = result * 8 + (digit - '0');
        }
        else if (!BigInteger.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out result)) return false;
        if (negative) result = -result;
        return true;
    }

    public static bool TryBoolean(YamlNode? node, out bool result)
    {
        result = false;
        return node is YamlScalarNode scalar && HasScalarType(scalar, "bool") && bool.TryParse(scalar.Value, out result);
    }

    private static bool HasScalarType(YamlScalarNode scalar, string type)
    {
        var tag = scalar.Tag.ToString();
        if (tag == $"tag:yaml.org,2002:{type}") return true;
        return (scalar.Tag.IsEmpty || tag == "?") && scalar.Style is ScalarStyle.Plain or ScalarStyle.Any;
    }

    public static (int Line, int Column) Location(YamlNode? node) => node == null ? (1, 1) :
        ((int)Math.Clamp(node.Start.Line + 1, 2, int.MaxValue), (int)Math.Clamp(node.Start.Column, 1, int.MaxValue));
}
