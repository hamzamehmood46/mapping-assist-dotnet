namespace Mapping.Core;

public sealed record SessionResult(bool Ok, string? Error)
{
    public static SessionResult Success { get; } = new(true, null);

    public static SessionResult Fail(string error) => new(false, error);
}

public sealed record ApplyResult(IReadOnlyList<KeyValuePair<string, string>> Values, IReadOnlyList<string> Skipped, Rendered Output);

/// <summary>
/// One mapping job: a source sample, a target schema and the suggestions made for them. A suggestion changes nothing until a person
/// approves it; only approved suggestions are used to build output. A person may change the target and the transform before approving.
/// </summary>
public sealed class MappingSession
{
    private readonly IMappingAssistant _assistant;
    private readonly List<Suggestion> _suggestions = [];
    private readonly Dictionary<string, SourceField> _source;
    private readonly Dictionary<string, TargetField> _target;
    private int _nextId = 1;

    public MappingSession(IReadOnlyList<SourceField> source, IReadOnlyList<TargetField> target, IMappingAssistant assistant)
    {
        Source = source;
        Target = target;
        _assistant = assistant;
        _source = source.ToDictionary(f => f.Path);
        _target = target.ToDictionary(f => f.Path);
    }

    public IReadOnlyList<SourceField> Source { get; }

    public IReadOnlyList<TargetField> Target { get; }

    /// <summary>A snapshot: changing it does not change the session.</summary>
    public IReadOnlyList<Suggestion> Suggestions => [.. _suggestions];

    public IReadOnlyList<Suggestion> Approved => [.. _suggestions.Where(s => s.Status == SuggestionStatus.Approved)];

    public IReadOnlyList<TargetField> UnmappedTargets =>
        [.. Target.Where(t => _suggestions.All(s => s.Status != SuggestionStatus.Approved || s.TargetPath != t.Path))];

    /// <summary>Asks the assistant for new proposals. Earlier pending ones are replaced; approved and rejected ones stay.</summary>
    public async Task<SessionResult> SuggestAsync(CancellationToken cancellationToken = default)
    {
        var outcome = await _assistant.SuggestAsync(Source, Target, cancellationToken).ConfigureAwait(false);
        if (outcome.Value is null)
        {
            return SessionResult.Fail(outcome.Reason ?? "No suggestions.");
        }

        _suggestions.RemoveAll(s => s.Status == SuggestionStatus.Pending);
        var taken = _suggestions.Where(s => s.Status == SuggestionStatus.Approved).Select(s => s.TargetPath).ToHashSet();
        var rejected = _suggestions.Where(s => s.Status == SuggestionStatus.Rejected).Select(s => (s.SourcePath, s.TargetPath)).ToHashSet();
        var added = 0;
        foreach (var p in outcome.Value)
        {
            if (!_source.ContainsKey(p.SourcePath) || !_target.ContainsKey(p.TargetPath) || taken.Contains(p.TargetPath) || rejected.Contains((p.SourcePath, p.TargetPath)))
            {
                continue;
            }

            _suggestions.Add(new Suggestion(_nextId++, p.SourcePath, p.TargetPath, p.Transform, p.Confidence, p.Rationale, SuggestionStatus.Pending));
            added++;
        }

        return added == 0 ? SessionResult.Fail("The assistant had no new suggestions.") : SessionResult.Success;
    }

    /// <summary>Approves a pending suggestion, optionally with a different target or transform. Refuses if the target is already mapped.</summary>
    public SessionResult Approve(int id, string? target = null, string? transform = null)
    {
        var index = _suggestions.FindIndex(s => s.Id == id);
        if (index < 0)
        {
            return SessionResult.Fail($"There is no suggestion {id}.");
        }

        var current = _suggestions[index];
        if (current.Status != SuggestionStatus.Pending)
        {
            return SessionResult.Fail($"Suggestion {id} is already {current.Status.ToString().ToLowerInvariant()}.");
        }

        var finalTarget = string.IsNullOrEmpty(target) ? current.TargetPath : target;
        var finalTransform = string.IsNullOrEmpty(transform) ? current.Transform : transform;
        if (!_target.ContainsKey(finalTarget))
        {
            return SessionResult.Fail($"'{finalTarget}' is not a field in the target schema.");
        }

        if (!Transforms.IsKnown(finalTransform))
        {
            return SessionResult.Fail($"'{finalTransform}' is not a known transform.");
        }

        if (_suggestions.Any(s => s.Status == SuggestionStatus.Approved && s.TargetPath == finalTarget))
        {
            return SessionResult.Fail($"'{finalTarget}' is already mapped. Reject the other mapping first.");
        }

        _suggestions[index] = current with { TargetPath = finalTarget, Transform = finalTransform, Status = SuggestionStatus.Approved };
        return SessionResult.Success;
    }

    public SessionResult Reject(int id)
    {
        var index = _suggestions.FindIndex(s => s.Id == id);
        if (index < 0)
        {
            return SessionResult.Fail($"There is no suggestion {id}.");
        }

        if (_suggestions[index].Status != SuggestionStatus.Pending)
        {
            return SessionResult.Fail($"Suggestion {id} is already {_suggestions[index].Status.ToString().ToLowerInvariant()}.");
        }

        _suggestions[index] = _suggestions[index] with { Status = SuggestionStatus.Rejected };
        return SessionResult.Success;
    }

    /// <summary>Withdraws an approval, so the mapping stops being used.</summary>
    public SessionResult Unapprove(int id)
    {
        var index = _suggestions.FindIndex(s => s.Id == id);
        if (index < 0 || _suggestions[index].Status != SuggestionStatus.Approved)
        {
            return SessionResult.Fail($"Suggestion {id} is not approved.");
        }

        _suggestions[index] = _suggestions[index] with { Status = SuggestionStatus.Rejected };
        return SessionResult.Success;
    }

    /// <summary>Builds the output from approved mappings only, in the order of the target schema.</summary>
    public ApplyResult Apply()
    {
        var values = new List<KeyValuePair<string, string>>();
        var skipped = new List<string>();
        foreach (var t in Target)
        {
            var s = _suggestions.FirstOrDefault(x => x.Status == SuggestionStatus.Approved && x.TargetPath == t.Path);
            if (s is null)
            {
                continue;
            }

            var converted = Transforms.Apply(s.Transform, _source[s.SourcePath].Value);
            if (converted is null)
            {
                skipped.Add($"{t.Path}: '{_source[s.SourcePath].Value}' cannot be converted with '{s.Transform}', so it was left out.");
                continue;
            }

            values.Add(new KeyValuePair<string, string>(t.Path, converted));
        }

        return new ApplyResult(values, skipped, OutputWriter.Render(values));
    }
}
