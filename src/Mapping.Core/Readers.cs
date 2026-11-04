using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Mapping.Core;

/// <summary>
/// Reads a source sample into leaf fields. XML and EDI-style segments (N1*BY*Name~) are supported. Only the first of a repeated
/// element or segment is read, so a mapping describes one line item, not a whole list. DTDs are refused, so a sample cannot pull in a file.
/// </summary>
public static partial class SourceReader
{
    // Names for the few EDI elements the demo knows about; every other element is addressed by position only.
    private static readonly Dictionary<string, string> EdiNames = new()
    {
        ["BEG.01"] = "PurposeCode",
        ["BEG.02"] = "OrderType",
        ["BEG.03"] = "PurchaseOrderNumber",
        ["BEG.05"] = "OrderDate",
        ["N1.01"] = "EntityCode",
        ["N1.02"] = "PartyName",
        ["N3.01"] = "StreetAddress",
        ["N4.01"] = "City",
        ["N4.02"] = "State",
        ["N4.03"] = "PostalCode",
        ["PO1.01"] = "LineNumber",
        ["PO1.02"] = "Quantity",
        ["PO1.03"] = "UnitOfMeasure",
        ["PO1.04"] = "UnitPrice",
        ["PO1.07"] = "ProductCode",
    };

    [GeneratedRegex(@"^[A-Z0-9]{2,3}$")]
    private static partial Regex SegmentId();

    public static Parsed<SourceField> Read(string? text, int maxFields = 200)
    {
        var t = (text ?? "").Trim();
        if (t.Length == 0)
        {
            return Parsed<SourceField>.Fail("Paste a source sample first.");
        }

        return t.StartsWith('<') ? ReadXml(t, maxFields) : ReadSegments(t, maxFields);
    }

    private static Parsed<SourceField> ReadXml(string text, int maxFields)
    {
        XDocument doc;
        try
        {
            using var reader = XmlReader.Create(new StringReader(text), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            doc = XDocument.Load(reader);
        }
        catch (XmlException)
        {
            return Parsed<SourceField>.Fail("The XML could not be read. It is malformed, or it has a DOCTYPE, which is refused.");
        }

        var fields = new List<SourceField>();
        Walk(doc.Root!, "", fields);
        if (fields.Count == 0)
        {
            return Parsed<SourceField>.Fail("The XML has no values to map.");
        }

        return fields.Count > maxFields ? Parsed<SourceField>.Fail($"The sample has {fields.Count} values; the limit is {maxFields}.") : new Parsed<SourceField>(fields, null);
    }

    private static void Walk(XElement element, string prefix, List<SourceField> fields)
    {
        var path = $"{prefix}/{element.Name.LocalName}";
        foreach (var attribute in element.Attributes().Where(a => !a.IsNamespaceDeclaration && a.Value.Trim().Length > 0))
        {
            fields.Add(new SourceField($"{path}/@{attribute.Name.LocalName}", attribute.Value.Trim()));
        }

        if (!element.HasElements)
        {
            var value = element.Value.Trim();
            if (value.Length > 0)
            {
                fields.Add(new SourceField(path, value));
            }

            return;
        }

        var seen = new HashSet<string>();
        foreach (var child in element.Elements())
        {
            if (seen.Add(child.Name.LocalName))
            {
                Walk(child, path, fields);
            }
        }
    }

    private static Parsed<SourceField> ReadSegments(string text, int maxFields)
    {
        var fields = new List<SourceField>();
        var seen = new HashSet<string>();
        foreach (var raw in text.Split(['~', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = raw.Split('*');
            var id = parts[0].Trim().ToUpperInvariant();
            if (!SegmentId().IsMatch(id))
            {
                return Parsed<SourceField>.Fail($"'{(parts[0].Length > 20 ? parts[0][..20] + "..." : parts[0])}' does not look like a segment id. Expected something like N1*BY*Acme~ or an XML document.");
            }

            if (!seen.Add(id))
            {
                continue;
            }

            for (var i = 1; i < parts.Length; i++)
            {
                var value = parts[i].Trim();
                if (value.Length == 0)
                {
                    continue;
                }

                var path = $"{id}.{i:00}";
                fields.Add(new SourceField(path, value, EdiNames.GetValueOrDefault(path)));
            }
        }

        if (fields.Count == 0)
        {
            return Parsed<SourceField>.Fail("The segments have no values to map.");
        }

        return fields.Count > maxFields ? Parsed<SourceField>.Fail($"The sample has {fields.Count} values; the limit is {maxFields}.") : new Parsed<SourceField>(fields, null);
    }
}

/// <summary>
/// Reads the target schema, one field per line: <c>path | type | description</c>. Type is string, number or date and may be left out.
/// A path is either an XML-style path (/Order/Id) or an EDI-style element (N1.02). Lines starting with # are comments.
/// </summary>
public static partial class SchemaReader
{
    [GeneratedRegex(@"^(/[^\s/|]+)+(/@[^\s/|]+)?$|^[A-Z0-9]{2,3}\.\d{2}$")]
    private static partial Regex ValidPath();

    private static readonly string[] Types = ["string", "number", "date"];

    public static Parsed<TargetField> Read(string? text, int maxFields = 200)
    {
        var fields = new List<TargetField>();
        var seen = new HashSet<string>();
        var lineNo = 0;
        foreach (var raw in (text ?? "").Replace("\r\n", "\n").Split('\n'))
        {
            lineNo++;
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var parts = line.Split('|', 3, StringSplitOptions.TrimEntries);
            var path = parts[0];
            if (!ValidPath().IsMatch(path))
            {
                return Parsed<TargetField>.Fail($"Line {lineNo}: '{path}' is not a path. Use /Order/Id for XML or N1.02 for an EDI element.");
            }

            var type = parts.Length > 1 && parts[1].Length > 0 ? parts[1].ToLowerInvariant() : "string";
            if (!Types.Contains(type))
            {
                return Parsed<TargetField>.Fail($"Line {lineNo}: type '{type}' is not string, number or date.");
            }

            if (!seen.Add(path))
            {
                return Parsed<TargetField>.Fail($"Line {lineNo}: '{path}' appears twice.");
            }

            fields.Add(new TargetField(path, type, parts.Length > 2 ? parts[2] : ""));
        }

        if (fields.Count == 0)
        {
            return Parsed<TargetField>.Fail("Describe the target first: one field per line, like /Order/Id | string | Order number.");
        }

        return fields.Count > maxFields ? Parsed<TargetField>.Fail($"The schema has {fields.Count} fields; the limit is {maxFields}.") : new Parsed<TargetField>(fields, null);
    }
}
