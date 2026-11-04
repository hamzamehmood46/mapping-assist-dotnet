namespace Mapping.Core;

/// <summary>A value found in the source sample. Label is a readable name for an element whose path says little, such as an EDI element.</summary>
public sealed record SourceField(string Path, string Value, string? Label = null);

/// <summary>A place the mapped value should land. Type is string, number or date.</summary>
public sealed record TargetField(string Path, string Type, string Description);

/// <summary>What a model proposes, before any checking.</summary>
public sealed record Proposal(string SourcePath, string TargetPath, string Transform, double Confidence, string Rationale);

public enum SuggestionStatus
{
    Pending,
    Approved,
    Rejected,
}

public sealed record Suggestion(int Id, string SourcePath, string TargetPath, string Transform, double Confidence, string Rationale, SuggestionStatus Status);

/// <summary>Either a list of items or a message saying why there is none. Parsing never throws on bad input.</summary>
public sealed record Parsed<T>(IReadOnlyList<T> Items, string? Error)
{
    public bool Ok => Error is null;

    public static Parsed<T> Fail(string error) => new([], error);
}

public sealed record Outcome<T>(T? Value, string? Reason) where T : class
{
    public static Outcome<T> Of(T value) => new(value, null);

    public static Outcome<T> None(string reason) => new(null, reason);
}

public sealed class MapOptions
{
    public int MaxFields { get; init; } = 200;

    /// <summary>Sample values are cut to this length before they are sent to a model.</summary>
    public int MaxValueChars { get; init; } = 60;

    public int MaxPromptChars { get; init; } = 24000;

    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);
}
