using Microsoft.Extensions.AI;

namespace Scout.Llm.Providers;

/// <summary>
/// Reads the Microsoft.Extensions.AI <c>UsageContent</c> a connector puts under the <c>Usage</c> metadata key, for the
/// ChatGPT plan, Anthropic and Azure AI Inference.
/// </summary>
internal static class UsageContentReader
{
    /// <summary>Adds the usage as one model call when it is a <c>UsageContent</c>; any other value is ignored.</summary>
    internal static void Count(object? usage, UsageTally tally)
    {
        if (usage is UsageContent content)
        {
            tally.Add(content.Details.InputTokenCount, content.Details.OutputTokenCount, content.Details.TotalTokenCount, null);
        }
    }
}
