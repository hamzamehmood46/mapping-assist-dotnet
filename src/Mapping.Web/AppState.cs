using Mapping.Core;

namespace Mapping.Web;

/// <summary>Holds the assistant for the page. The browser can only use the stand-in model, because a static page cannot safely hold a key.</summary>
public sealed class AppState
{
    public IMappingAssistant Assistant { get; } = new MappingAssistant(new FakeChatClient());
}
