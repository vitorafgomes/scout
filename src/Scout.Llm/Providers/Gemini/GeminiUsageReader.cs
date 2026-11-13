using Microsoft.SemanticKernel.Connectors.Google;

namespace Scout.Llm.Providers.Gemini;

/// <summary>
/// Reads the running <c>usageMetadata</c> the Google connector repeats on every chunk of a Gemini call: the last one of
/// a call counts, and a call ends at the chunk that carries a finish reason.
/// </summary>
internal static class GeminiUsageReader
{
    /// <summary>
    /// True when the metadata is the Google connector's: its running count becomes the pending one, committed when the
    /// chunk carries a finish reason. False for any other metadata, which is left to the other readers.
    /// </summary>
    internal static bool Count(IReadOnlyDictionary<string, object?>? metadata, UsageTally tally)
    {
        if (metadata is not GeminiMetadata gemini)
        {
            return false;
        }

        tally.SetPending(gemini.PromptTokenCount, gemini.CandidatesTokenCount, gemini.TotalTokenCount);
        if (!string.IsNullOrEmpty(gemini.FinishReason?.ToString()))
        {
            tally.CommitPending();
        }

        return true;
    }
}
