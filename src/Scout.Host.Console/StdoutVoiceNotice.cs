using System.Text.Json;
using Scout.Conversation.Abstractions;

namespace Scout.Host.Console;

/// <summary>
/// Writes one <c>SCOUT_LOCAL {"id":null,"event":"voice-no-model","platform":...}</c> line to stdout, outside the
/// logging pipeline, when a voice message arrives while no speech model is installed, so the desktop offers the
/// download in its notification center.
/// </summary>
internal sealed class StdoutVoiceNotice(TextWriter? writer = null) : IVoiceNoticeSignal
{
    private const string Prefix = "SCOUT_LOCAL ";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public void NoSpeechModel(string platform)
    {
        var output = writer ?? System.Console.Out;
        output.WriteLine(Prefix + JsonSerializer.Serialize(new VoiceEvent(null, "voice-no-model", platform), Json));
        output.Flush();
    }

    private sealed record VoiceEvent(string? Id, string Event, string Platform);
}
