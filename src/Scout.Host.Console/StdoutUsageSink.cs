using System.Text.Json;
using System.Text.Json.Serialization;
using Scout.Llm.Abstractions;

namespace Scout.Host.Console;

/// <summary>
/// Writes one <c>SCOUT_LOCAL &lt;json&gt;</c> line per answer (<c>usage</c>) and per plan usage limit
/// (<c>usage-limit</c>) to stdout, outside the logging pipeline, with a null id, so the desktop counts what each AI
/// source used whichever chat client asked.
/// </summary>
internal sealed class StdoutUsageSink(TextWriter? writer = null) : ILlmUsageSink
{
    private const string Prefix = "SCOUT_LOCAL ";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public void Answered(LlmUsage usage)
        => Write(new UsageEvent(
            null, "usage", usage.Source, usage.Model, usage.PromptTokens, usage.CompletionTokens, usage.TotalTokens, usage.Cost));

    public void UsageLimitReached(string source) => Write(new UsageEvent(null, "usage-limit", source));

    private void Write(UsageEvent usageEvent)
    {
        var output = writer ?? System.Console.Out;
        output.WriteLine(Prefix + JsonSerializer.Serialize(usageEvent, Json));
        output.Flush();
    }

    private sealed record UsageEvent(
        [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Id,
        string Event,
        string Source,
        string? Model = null,
        long? PromptTokens = null,
        long? CompletionTokens = null,
        long? TotalTokens = null,
        decimal? Cost = null);
}
