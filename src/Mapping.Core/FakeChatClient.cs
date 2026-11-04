using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;

namespace Mapping.Core;

/// <summary>
/// A stand-in for a language model. It compares field names: it splits them into words, treats a few common abbreviations as the same word,
/// and pairs each target with the best remaining source if enough words are shared. It also checks that the value looks like the target's
/// type (a date for a date, a number for a number). It does not understand meaning, so "Buyer" and "Customer" match only because of one
/// built-in word list. It is deterministic, needs no network or key, and can also replay scripted replies for tests.
/// </summary>
public sealed partial class FakeChatClient : IChatClient
{
    public const double MinScore = 0.34;

    // Words treated as the same word. Deliberately small: this is a demonstration, not a thesaurus.
    private static readonly string[][] Groups =
    [
        ["qty", "quantity", "count", "units"],
        ["amt", "amount", "total", "price", "cost"],
        ["addr", "address", "street"],
        ["num", "number", "no", "id", "identifier"],
        ["desc", "description", "notes", "note", "comment"],
        ["tel", "phone", "telephone"],
        ["zip", "postal", "postcode"],
        ["dt", "date"],
        ["cust", "customer", "buyer"],
        ["sku", "product", "item"],
        ["po", "purchase"],
    ];

    private static readonly Dictionary<string, string> Canonical = Groups.SelectMany(g => g.Select(w => (Word: w, Head: g[0]))).ToDictionary(p => p.Word, p => p.Head);

    private readonly Queue<Func<string>> _script = new();
    private readonly List<IReadOnlyList<ChatMessage>> _requests = [];

    public IReadOnlyList<IReadOnlyList<ChatMessage>> Requests => _requests;

    public FakeChatClient Script(params string[] replies)
    {
        foreach (var r in replies)
        {
            _script.Enqueue(() => r);
        }

        return this;
    }

    public FakeChatClient ScriptThrow(Exception ex)
    {
        _script.Enqueue(() => throw ex);
        return this;
    }

    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var list = messages.ToList();
        _requests.Add(list);
        var reply = _script.Count > 0 ? _script.Dequeue()() : Answer(list.LastOrDefault(m => m.Role == ChatRole.User && m.Text.Contains("<source>"))?.Text ?? "");
        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, reply)));
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
        yield return new ChatResponseUpdate(ChatRole.Assistant, response.Text);
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;

    public void Dispose()
    {
    }

    [GeneratedRegex(@"<source>\n(?<s>.*?)</source>\n<target>\n(?<t>.*?)</target>", RegexOptions.Singleline)]
    private static partial Regex Blocks();

    [GeneratedRegex(@"[A-Z]+(?![a-z])|[A-Z]?[a-z]+|\d+")]
    private static partial Regex Words();

    private sealed record Src(string Path, string? Label, string Value);

    private sealed record Tgt(string Path, string Type, string Description);

    private static string Answer(string prompt)
    {
        var m = Blocks().Match(prompt);
        var sources = Lines(m.Groups["s"].Value).Select(o => new Src(Get(o, "path"), o["label"]?.GetValue<string>(), Get(o, "value"))).ToList();
        var targets = Lines(m.Groups["t"].Value).Select(o => new Tgt(Get(o, "path"), Get(o, "type"), Get(o, "description"))).ToList();

        var pairs = new List<(Src S, Tgt T, double Score, string Why)>();
        foreach (var t in targets)
        {
            foreach (var s in sources)
            {
                var (score, why) = Score(s, t);
                if (score >= MinScore)
                {
                    pairs.Add((s, t, score, why));
                }
            }
        }

        var usedSources = new HashSet<string>();
        var usedTargets = new HashSet<string>();
        var mappings = new JsonArray();
        foreach (var p in pairs.OrderByDescending(p => p.Score).ThenBy(p => p.T.Path, StringComparer.Ordinal).ThenBy(p => p.S.Path, StringComparer.Ordinal))
        {
            if (usedSources.Contains(p.S.Path) || usedTargets.Contains(p.T.Path))
            {
                continue;
            }

            usedSources.Add(p.S.Path);
            usedTargets.Add(p.T.Path);
            mappings.Add(new JsonObject
            {
                ["source"] = p.S.Path,
                ["target"] = p.T.Path,
                ["transform"] = PickTransform(p.S, p.T),
                ["confidence"] = Math.Round(Math.Min(0.95, p.Score), 2),
                ["rationale"] = p.Why,
            });
        }

        return new JsonObject { ["mappings"] = mappings }.ToJsonString();
    }

    private static IEnumerable<JsonObject> Lines(string block) =>
        block.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(l => (JsonObject)JsonNode.Parse(l)!);

    private static string Get(JsonObject o, string key) => o[key]?.GetValue<string>() ?? "";

    [GeneratedRegex(@"^[A-Z0-9]{2,3}\.\d{2}$")]
    private static partial Regex EdiElement();

    private static (double Score, string Why) Score(Src s, Tgt t)
    {
        // An EDI element with no known name is only a position, so there is nothing to compare.
        if (s.Label is not { Length: > 0 } && EdiElement().IsMatch(s.Path))
        {
            return (0, "");
        }

        var a = TokenMap(s.Label is { Length: > 0 } ? s.Label : LastSegments(s.Path));
        var b = TokenMap(LastSegments(t.Path));
        var d = TokenMap(t.Description);
        if (a.Count == 0)
        {
            return (0, "");
        }

        var shared = a.Keys.Intersect(b.Keys).ToList();
        var byName = Dice(a.Count, b.Count, shared.Count);
        var sharedDesc = a.Keys.Intersect(d.Keys).ToList();
        var byDescription = 0.9 * Dice(a.Count, d.Count, sharedDesc.Count);
        var baseScore = Math.Max(byName, byDescription);
        var words = (byName >= byDescription ? shared : sharedDesc).Select(k => a[k]);
        if (baseScore < 0.2)
        {
            return (0, "");
        }

        var kind = Kind(s.Value);
        var reason = $"Field names share '{string.Join("', '", words)}'";
        var score = baseScore;
        switch (t.Type)
        {
            case "date" when kind == "date":
                score += 0.25;
                reason += "; the value looks like a date";
                break;
            case "number" when kind == "number":
                score += 0.15;
                reason += "; the value looks like a number";
                break;
            case "date" or "number":
                score *= 0.25;
                reason += $"; but the value does not look like a {t.Type}";
                break;
        }

        return (score, reason + ".");
    }
    private static double Dice(int a, int b, int shared) => a + b == 0 ? 0 : 2.0 * shared / (a + b);

    private static string Kind(string value) => Transforms.LooksLikeDate(value) ? "date" : Transforms.LooksLikeNumber(value) ? "number" : "string";

    private static string PickTransform(Src s, Tgt t)
    {
        if (t.Type == "date" && Transforms.LooksLikeDate(s.Value) && !Transforms.IsIsoDate(s.Value))
        {
            return "isodate";
        }

        if (t.Type == "number" && Transforms.LooksLikeNumber(s.Value) && !Transforms.IsPlainNumber(s.Value))
        {
            return "number";
        }

        var d = t.Description.ToLowerInvariant();
        return d.Contains("uppercase") ? "upper" : d.Contains("lowercase") ? "lower" : "copy";
    }

    private static string LastSegments(string path)
    {
        var parts = path.Split(['/', '.'], StringSplitOptions.RemoveEmptyEntries).Select(p => p.TrimStart('@')).ToList();
        return string.Join(' ', parts.Skip(Math.Max(0, parts.Count - 2)));
    }

    /// <summary>Word groups to the first word the text used for each, so a reason can quote the field's own words.</summary>
    private static Dictionary<string, string> TokenMap(string text)
    {
        var map = new Dictionary<string, string>();
        foreach (var word in Words().Matches(text).Select(m => m.Value.ToLowerInvariant()).Where(w => w.Length > 1))
        {
            map.TryAdd(Canonical.GetValueOrDefault(word, word), word);
        }

        return map;
    }
}