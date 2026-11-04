using System.Net;
using System.Text;
using Microsoft.Extensions.AI;

namespace Mapping.Core.Tests;

public class ProviderTests
{
    private sealed class Stub(Func<string> respond, HttpStatusCode code = HttpStatusCode.OK) : HttpMessageHandler
    {
        public List<(HttpRequestMessage Request, string Body)> Seen { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Seen.Add((request, request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken)));
            return new HttpResponseMessage(code) { Content = new StringContent(respond(), Encoding.UTF8, "application/json") };
        }
    }

    [Fact]
    public void The_fake_model_is_the_default()
    {
        Assert.Equal("fake", MapSettings.FromEnvironment(_ => null).Provider);
        Assert.IsType<FakeChatClient>(MapClients.Create(MapSettings.FromEnvironment(_ => null)));
    }

    [Fact]
    public void Settings_come_from_MAP_AI_variables_and_the_key_is_not_printed()
    {
        var env = new Dictionary<string, string> { ["MAP_AI_PROVIDER"] = "OpenAI-Compatible", ["MAP_AI_MODEL"] = "m", ["MAP_AI_ENDPOINT"] = "https://x.test/v1", ["MAP_AI_API_KEY"] = "topsecret" };
        var s = MapSettings.FromEnvironment(k => env.GetValueOrDefault(k));
        Assert.Equal("openai-compatible", s.Provider);
        Assert.DoesNotContain("topsecret", s.ToString());
    }

    [Fact]
    public void Unknown_providers_and_missing_settings_are_refused()
    {
        Assert.Throws<ArgumentException>(() => MapClients.Create(new MapSettings { Provider = "pigeon" }));
        Assert.Throws<ArgumentException>(() => MapClients.Create(new MapSettings { Provider = "ollama" }));
        Assert.Throws<ArgumentException>(() => MapClients.Create(new MapSettings { Provider = "openai-compatible", Model = "m" }));
    }

    [Fact]
    public async Task Ollama_posts_to_api_chat()
    {
        var stub = new Stub(() => """{"message":{"content":"hi"}}""");
        var r = await new OllamaChatClient(new HttpClient(stub), "llama3").GetResponseAsync([new ChatMessage(ChatRole.User, "x")]);
        Assert.Equal("hi", r.Text);
        Assert.Equal("http://localhost:11434/api/chat", stub.Seen.Single().Request.RequestUri!.ToString());
    }

    [Fact]
    public async Task A_hosted_client_sends_the_key_only_in_the_header()
    {
        var stub = new Stub(() => """{"choices":[{"message":{"content":"yo"}}]}""");
        var r = await new OpenAiCompatibleChatClient(new HttpClient(stub), "https://api.example.test/v1", "m", "k123").GetResponseAsync([new ChatMessage(ChatRole.User, "x")]);
        Assert.Equal("yo", r.Text);
        Assert.Equal("k123", stub.Seen.Single().Request.Headers.Authorization!.Parameter);
        Assert.DoesNotContain("k123", stub.Seen.Single().Body);
    }

    [Fact]
    public async Task A_server_error_means_no_proposals()
    {
        var chat = new OpenAiCompatibleChatClient(new HttpClient(new Stub(() => "{}", HttpStatusCode.InternalServerError)), "https://api.example.test/v1", "m", null);
        var o = await new MappingAssistant(chat).SuggestAsync(Rig.Src(), Rig.Tgt());
        Assert.Null(o.Value);
    }

    [Fact]
    public async Task A_stubbed_hosted_answer_goes_through_the_same_checks()
    {
        var reply = System.Text.Json.JsonSerializer.Serialize(new { choices = new[] { new { message = new { content = Rig.Reply(("/PurchaseOrder/ShipTo/City", "/Order/ShipTo/City", "copy", 0.9, "City.")) } } } });
        var chat = new OpenAiCompatibleChatClient(new HttpClient(new Stub(() => reply)), "https://api.example.test/v1", "m", null);
        var o = await new MappingAssistant(chat).SuggestAsync(Rig.Src(), Rig.Tgt());
        Assert.Single(o.Value!);
    }
}
