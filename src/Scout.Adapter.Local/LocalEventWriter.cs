using System.Text.Json;
using System.Text.Json.Serialization;
using Scout.Conversation.Abstractions;

namespace Scout.Adapter.Local;

/// <summary>
/// Writes one <c>SCOUT_LOCAL &lt;json&gt;</c> line per event to the worker's stdout.
/// The JSON escapes every line break, so reply text can never start a line of
/// its own, and each event is a single <see cref="TextWriter.WriteLine(string)"/>
/// on a synchronized writer, so lines from concurrent replies and from the host's
/// readiness and pairing markers never interleave.
/// </summary>
internal sealed class LocalEventWriter(TextWriter output)
{
    internal const string Prefix = "SCOUT_LOCAL ";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly TextWriter _output = TextWriter.Synchronized(output);

    public void Write(string? id, string kind, string? text = null, IReadOnlyList<Citation>? citations = null)
    {
        var json = JsonSerializer.Serialize(
            new LocalEvent(id, kind, text, citations?.Select(c => new LocalCitation(c.Label, c.Url)).ToArray()),
            Json);
        _output.WriteLine(Prefix + json);
        _output.Flush();
    }

    private sealed record LocalEvent(
        [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Id,
        string Event,
        string? Text,
        IReadOnlyList<LocalCitation>? Citations);

    private sealed record LocalCitation(string Label, string? Url);
}
