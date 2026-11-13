using System.ClientModel.Primitives;
using System.Text.Json;
using OpenAI.Chat;

namespace Scout.Llm.Providers.OpenAI;

/// <summary>
/// Reads the usage the OpenAI connector puts under the <c>Usage</c> metadata key, an OpenAI <c>ChatTokenUsage</c>, for
/// OpenAI, Azure OpenAI, OpenRouter and Ollama, with the cost OpenRouter adds to it as an additional property.
/// </summary>
internal static class OpenAIChatUsageReader
{
    /// <summary>Adds the usage as one model call when it is a <c>ChatTokenUsage</c>; any other value is ignored.</summary>
    internal static void Count(object? usage, UsageTally tally)
    {
        if (usage is ChatTokenUsage chat)
        {
            tally.Add(chat.InputTokenCount, chat.OutputTokenCount, chat.TotalTokenCount, CostOf(chat));
        }
    }

    private static decimal? CostOf(ChatTokenUsage usage)
    {
        using var json = JsonDocument.Parse(ModelReaderWriter.Write(usage));
        return json.RootElement.TryGetProperty("cost", out var cost) && cost.ValueKind == JsonValueKind.Number
            ? cost.GetDecimal()
            : null;
    }
}
