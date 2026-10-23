using Microsoft.Extensions.AI;
using OpenAI.Responses;

namespace Scout.Llm.Providers.ChatGptPlan;

/// <summary>
/// Shapes every request into the form the ChatGPT plan accepts on the Responses
/// API: system messages become <see cref="ChatOptions.Instructions"/>, temperature
/// and max output tokens are dropped, the response is never stored (encrypted
/// reasoning is requested instead so it can be replayed), and a
/// non-streaming call is served by aggregating a stream.
/// </summary>
internal sealed class ChatGptPlanChatClient(IChatClient inner) : DelegatingChatClient(inner)
{
    public override Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
        => GetStreamingResponseAsync(messages, options, cancellationToken)
            .ToChatResponseAsync(cancellationToken);

    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var all = messages.ToList();
        var system = string.Join(
            "\n\n",
            all.Where(m => m.Role == ChatRole.System)
                .Select(m => m.Text)
                .Where(text => !string.IsNullOrWhiteSpace(text)));

        var shaped = options?.Clone() ?? new ChatOptions();
        if (system.Length > 0)
        {
            shaped.Instructions = string.IsNullOrWhiteSpace(shaped.Instructions)
                ? system
                : shaped.Instructions + "\n\n" + system;
        }

        shaped.Temperature = null;
        shaped.MaxOutputTokens = null;
        shaped.RawRepresentationFactory = _ => NotStored();

        return base.GetStreamingResponseAsync(
            all.Where(m => m.Role != ChatRole.System), shaped, cancellationToken);
    }

    // OPENAI001: the Responses API types are marked experimental in the OpenAI SDK.
    // The suppression is scoped to this one construction.
#pragma warning disable OPENAI001
    private static CreateResponseOptions NotStored()
    {
        var options = new CreateResponseOptions { StoredOutputEnabled = false };
        options.IncludedProperties.Add(IncludedResponseProperty.ReasoningEncryptedContent);
        return options;
    }
#pragma warning restore OPENAI001
}
