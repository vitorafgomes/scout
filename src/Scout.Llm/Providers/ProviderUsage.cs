using Scout.Llm.Providers.Gemini;
using Scout.Llm.Providers.OpenAI;

namespace Scout.Llm.Providers;

/// <summary>
/// Hands the metadata of one stream update to the reader of its usage shape, chosen by the shape of the metadata,
/// never by the provider's name: Gemini's metadata first, then the value under the <c>Usage</c> key.
/// </summary>
internal static class ProviderUsage
{
    /// <summary>Counts the usage one stream update carries into <paramref name="tally"/>; metadata of no known shape counts nothing.</summary>
    internal static void Count(IReadOnlyDictionary<string, object?>? metadata, UsageTally tally)
    {
        if (GeminiUsageReader.Count(metadata, tally))
        {
            return;
        }

        if (metadata is null || !metadata.TryGetValue("Usage", out var usage))
        {
            return;
        }

        OpenAIChatUsageReader.Count(usage, tally);
        UsageContentReader.Count(usage, tally);
    }
}
