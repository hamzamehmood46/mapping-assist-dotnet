using System.Text.RegularExpressions;

namespace Mapping.Core;

/// <summary>Replaces email addresses and phone numbers in a sample value before it leaves the process. Names and street addresses are not detected.</summary>
public static partial class Redactor
{
    [GeneratedRegex(@"[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}")]
    private static partial Regex Email();

    [GeneratedRegex(@"(?<!\w)\+?\(?\d[\d\s().\-]{8,}\d(?!\w)")]
    private static partial Regex Phone();

    public static string Redact(string text) => Phone().Replace(Email().Replace(text, "[email]"), "[phone]");
}
