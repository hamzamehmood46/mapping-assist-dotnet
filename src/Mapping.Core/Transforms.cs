using System.Globalization;
using System.Text.RegularExpressions;

namespace Mapping.Core;

/// <summary>The only value changes a mapping may ask for. A model can pick one of these names and nothing else, so it cannot supply code.</summary>
public static partial class Transforms
{
    public static readonly IReadOnlyList<string> Names = ["copy", "trim", "upper", "lower", "isodate", "number"];

    private static readonly string[] DateFormats =
    [
        "yyyyMMdd", "yyyy-MM-dd", "MM/dd/yyyy", "M/d/yyyy", "dd-MMM-yyyy", "d-MMM-yyyy", "yyyy-MM-ddTHH:mm:ss", "yyyy-MM-ddTHH:mm:ssZ", "yyyy/MM/dd",
    ];

    [GeneratedRegex(@"^[\$€£]?\s*-?[\d,]*\.?\d+$")]
    private static partial Regex LooksNumeric();

    public static bool IsKnown(string name) => Names.Contains(name);

    /// <summary>Returns null when the value cannot be converted, so the caller can report it instead of writing something wrong.</summary>
    public static string? Apply(string name, string value) => name switch
    {
        "copy" => value,
        "trim" => value.Trim(),
        "upper" => value.ToUpperInvariant(),
        "lower" => value.ToLowerInvariant(),
        "isodate" => TryDate(value),
        "number" => TryNumber(value),
        _ => null,
    };

    public static bool LooksLikeDate(string value) => TryDate(value) is not null;

    public static bool LooksLikeNumber(string value) => TryNumber(value) is not null;

    public static bool IsPlainNumber(string value) => decimal.TryParse(value, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out _) && !value.Contains(' ');

    public static bool IsIsoDate(string value) => DateTime.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _);

    private static string? TryDate(string value) =>
        DateTime.TryParseExact(value.Trim(), DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : null;

    private static string? TryNumber(string value)
    {
        var v = value.Trim();
        if (!LooksNumeric().IsMatch(v))
        {
            return null;
        }

        var cleaned = v.Replace("$", "").Replace("€", "").Replace("£", "").Replace(",", "").Trim();
        return decimal.TryParse(cleaned, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var n) ? n.ToString(CultureInfo.InvariantCulture) : null;
    }
}
