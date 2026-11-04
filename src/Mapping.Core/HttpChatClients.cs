using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;

namespace Mapping.Core;

/// <summary>A local Ollama server (default http://localhost:11434), spoken to over plain HTTP so no extra package is needed.</summary>
public sealed class OllamaChatClient : IChatClient
{
    private readonly HttpClient _http;
    private readonly string _model;
    private readonly Uri _chatUrl;

    public OllamaChatClient(HttpClient http, string model, string endpoint = "http://localhost:11434")
    {
        _http = http;
        _model = model;
        _chatUrl = new Uri(Slash(endpoint), "api/chat");
    }

    public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var body = new JsonObject
        {
            ["model"] = _model,
            ["stream"] = false,
            ["messages"] = Messages(messages),
            ["options"] = new JsonObject { ["temperature"] = options?.Temperature ?? 0f },
        };
        if (options?.ResponseFormat is ChatResponseFormatJson json)
        {
            body["format"] = json.Schema is { } schema ? JsonNode.Parse(schema.GetRawText()) : "json";
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, _chatUrl) { Content = Body(body) };
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false), cancellationToken: cancellationToken).ConfigureAwait(false);
        var text = doc.RootElement.GetProperty("message").GetProperty("content").GetString() ?? "";
        return new ChatResponse(new ChatMessage(ChatRole.Assistant, text));
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

    // JsonObject rather than anonymous types: no reflection, so it survives trimming in a published Blazor app.
    internal static JsonArray Messages(IEnumerable<ChatMessage> messages) =>
        new([.. messages.Select(m => (JsonNode?)new JsonObject { ["role"] = m.Role.Value, ["content"] = m.Text })]);

    internal static StringContent Body(JsonObject body) => new(body.ToJsonString(), Encoding.UTF8, "application/json");

    internal static Uri Slash(string endpoint) => new(endpoint.EndsWith('/') ? endpoint : endpoint + "/");
}

/// <summary>
/// Any hosted service that offers the common "chat completions" HTTP shape. The key is sent as a bearer token and is never logged.
/// This path is covered by tests against a stub server only; it has not been run against a live provider.
/// </summary>
public sealed class OpenAiCompatibleChatClient : IChatClient
{
    private readonly HttpClient _http;
    private readonly string _model;
    private readonly string? _apiKey;
    private readonly Uri _chatUrl;

    public OpenAiCompatibleChatClient(HttpClient http, string endpoint, string model, string? apiKey)
    {
        _http = http;
        _model = model;
        _apiKey = apiKey;
        _chatUrl = new Uri(OllamaChatClient.Slash(endpoint), "chat/completions");
    }

    public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var body = new JsonObject
        {
            ["model"] = _model,
            ["temperature"] = options?.Temperature ?? 0f,
            ["messages"] = OllamaChatClient.Messages(messages),
        };
        if (options?.ResponseFormat is ChatResponseFormatJson)
        {
            body["response_format"] = new JsonObject { ["type"] = "json_object" };
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, _chatUrl) { Content = OllamaChatClient.Body(body) };
        if (!string.IsNullOrEmpty(_apiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        }

        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false), cancellationToken: cancellationToken).ConfigureAwait(false);
        var text = doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "";
        return new ChatResponse(new ChatMessage(ChatRole.Assistant, text));
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
}

/// <summary>Which provider to use, read from environment variables so no endpoint or key ever lives in code.</summary>
public sealed class MapSettings
{
    public string Provider { get; init; } = "fake";
    public string? Endpoint { get; init; }
    public string? Model { get; init; }
    public string? ApiKey { get; init; }

    /// <summary>MAP_AI_PROVIDER (fake, ollama or openai-compatible), MAP_AI_ENDPOINT, MAP_AI_MODEL, MAP_AI_API_KEY.</summary>
    public static MapSettings FromEnvironment(Func<string, string?>? read = null)
    {
        read ??= Environment.GetEnvironmentVariable;
        return new MapSettings
        {
            Provider = (read("MAP_AI_PROVIDER") is { Length: > 0 } p ? p : "fake").Trim().ToLowerInvariant(),
            Endpoint = read("MAP_AI_ENDPOINT"),
            Model = read("MAP_AI_MODEL"),
            ApiKey = read("MAP_AI_API_KEY"),
        };
    }

    public override string ToString() =>
        $"Provider={Provider}, Endpoint={Endpoint ?? "(default)"}, Model={Model ?? "(none)"}, ApiKey={(string.IsNullOrEmpty(ApiKey) ? "(none)" : "(set)")}";
}

public static class MapClients
{
    public static IChatClient Create(MapSettings settings, HttpClient? http = null)
    {
        switch (settings.Provider)
        {
            case "fake":
                return new FakeChatClient();
            case "ollama":
                return new OllamaChatClient(http ?? new HttpClient(), Require(settings.Model, "MAP_AI_MODEL"), settings.Endpoint is { Length: > 0 } e ? e : "http://localhost:11434");
            case "openai-compatible":
                return new OpenAiCompatibleChatClient(http ?? new HttpClient(), Require(settings.Endpoint, "MAP_AI_ENDPOINT"), Require(settings.Model, "MAP_AI_MODEL"), settings.ApiKey);
            default:
                throw new ArgumentException($"Unknown provider '{settings.Provider}'. Use fake, ollama or openai-compatible.");
        }
    }

    private static string Require(string? value, string name) =>
        string.IsNullOrWhiteSpace(value) ? throw new ArgumentException($"{name} is required for this provider.") : value;
}
