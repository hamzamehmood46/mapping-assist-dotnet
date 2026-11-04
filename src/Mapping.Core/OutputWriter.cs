using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Mapping.Core;

public sealed record Rendered(string Text, IReadOnlyList<string> Warnings);

/// <summary>Turns target path/value pairs into a document: XML for paths like /Order/Id, EDI-style segments for paths like N1.02.</summary>
public static partial class OutputWriter
{
    [GeneratedRegex(@"^([A-Z0-9]{2,3})\.(\d{2})$")]
    private static partial Regex Element();

    public static Rendered Render(IReadOnlyList<KeyValuePair<string, string>> values)
    {
        if (values.Count == 0)
        {
            return new Rendered("", []);
        }

        return values[0].Key.StartsWith('/') ? Xml(values) : Segments(values);
    }

    private static Rendered Xml(IReadOnlyList<KeyValuePair<string, string>> values)
    {
        var warnings = new List<string>();
        XElement? root = null;
        foreach (var (path, value) in values)
        {
            var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (!path.StartsWith('/') || parts.Any(p => !IsName(p.TrimStart('@'))))
            {
                warnings.Add($"{path} was skipped: it is not a valid XML path.");
                continue;
            }

            root ??= new XElement(parts[0]);
            if (root.Name.LocalName != parts[0])
            {
                warnings.Add($"{path} was skipped: it is not under <{root.Name.LocalName}>.");
                continue;
            }

            var current = root;
            var lastIsAttribute = parts[^1].StartsWith('@');
            var walk = lastIsAttribute ? parts[1..^1] : parts[1..];
            foreach (var name in walk)
            {
                var child = current.Element(name);
                if (child is null)
                {
                    child = new XElement(name);
                    current.Add(child);
                }

                current = child;
            }

            if (lastIsAttribute)
            {
                current.SetAttributeValue(parts[^1][1..], value);
            }
            else if (current.HasElements)
            {
                warnings.Add($"{path} was skipped: it already has child elements.");
            }
            else
            {
                current.Value = value;
            }
        }

        return new Rendered(root?.ToString() ?? "", warnings);
    }

    private static Rendered Segments(IReadOnlyList<KeyValuePair<string, string>> values)
    {
        var warnings = new List<string>();
        var order = new List<string>();
        var segments = new Dictionary<string, SortedDictionary<int, string>>();
        foreach (var (path, value) in values)
        {
            var m = Element().Match(path);
            if (!m.Success)
            {
                warnings.Add($"{path} was skipped: it is not an EDI element like N1.02.");
                continue;
            }

            var id = m.Groups[1].Value;
            if (!segments.TryGetValue(id, out var elements))
            {
                segments[id] = elements = [];
                order.Add(id);
            }

            if (value.Contains('*') || value.Contains('~'))
            {
                warnings.Add($"{path}: '*' and '~' in the value were replaced with spaces.");
            }

            elements[int.Parse(m.Groups[2].Value)] = value.Replace('*', ' ').Replace('~', ' ');
        }

        var lines = order.Select(id =>
        {
            var e = segments[id];
            return id + "*" + string.Join('*', Enumerable.Range(1, e.Keys.Max()).Select(i => e.GetValueOrDefault(i, ""))) + "~";
        });
        return new Rendered(string.Join('\n', lines), warnings);
    }

    private static bool IsName(string name)
    {
        try
        {
            return name.Length > 0 && XmlConvert.VerifyName(name) == name && !name.Contains(':');
        }
        catch (XmlException)
        {
            return false;
        }
    }
}
