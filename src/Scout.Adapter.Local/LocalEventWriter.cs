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

    public void Write(string? id, string kind, string? text = null, IReadOnlyList<Citation>? citations = null, int? seq = null)
        => Write(new LocalEvent(id, kind, text, citations?.Select(c => new LocalCitation(c.Label, c.Url)).ToArray(), seq));

    public void WriteTranslation(string id, LiveTranslation translation)
        => Write(new LocalEvent(
            id, "translation", translation.Text, null, translation.Seq, translation.Language, translation.Error));

    public void WriteMeetingEvent(
        string id, string kind, string? meetingId = null, string? text = null, IReadOnlyList<MeetingSummary>? items = null, int? count = null)
        => Write(new LocalEvent(id, kind, text, null, MeetingId: meetingId, Items: items, Count: count));

    private void Write(LocalEvent localEvent)
    {
        _output.WriteLine(Prefix + JsonSerializer.Serialize(localEvent, Json));
        _output.Flush();
    }

    private sealed record LocalEvent(
        [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Id,
        string Event,
        string? Text,
        IReadOnlyList<LocalCitation>? Citations,
        int? Seq = null,
        string? Language = null,
        string? Error = null,
        string? MeetingId = null,
        IReadOnlyList<MeetingSummary>? Items = null,
        int? Count = null);

    private sealed record LocalCitation(string Label, string? Url);
}
