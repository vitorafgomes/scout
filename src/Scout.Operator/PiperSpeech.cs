using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace Scout.Operator;

/// <summary>One bundled voice: its id, the label the owner reads, its model file and its speaker in a model of several.</summary>
internal sealed record PiperVoice(string Id, string Label, string Model, int? Speaker);

/// <summary>
/// The bundled voices: the <c>piper</c> program at <see cref="OperatorMediaOptions.PiperPath"/> and the
/// <c>&lt;id&gt;.onnx</c> models beside their <c>&lt;id&gt;.onnx.json</c> in <see cref="OperatorMediaOptions.VoicesPath"/>.
/// A model of one speaker is the voice <c>piper:&lt;id&gt;</c>; a model of several is one voice
/// <c>piper:&lt;id&gt;#&lt;speaker&gt;</c> per speaker. Voices found are kept for the life of the process; none found is
/// answered again for <see cref="RetryAfter"/>, then looked for again. Narration runs <c>piper</c> with an argument list
/// and the text on its standard input, writing one file in the media directory through <see cref="MediaFiles"/>; nothing
/// else is run and nothing goes over the network.
/// </summary>
internal sealed partial class PiperSpeech(IProcessRunner runner, IOptions<OperatorMediaOptions> options, TimeProvider time)
{
    internal const string Prefix = "piper:";

    internal const string Source = "piper";

    internal static readonly TimeSpan RetryAfter = TimeSpan.FromMinutes(2);

    internal static string UnknownVoice(string id) =>
        $"The narration voice '{id}' is not available on this machine; choose another in Scout Desktop, Production.";

    internal static string Failed(int order) => $"Scout could not make the narration of scene {order} with the bundled voice";

    private readonly Lock _lock = new();
    private IReadOnlyList<PiperVoice>? _voices;
    private DateTimeOffset _probedAt;

    /// <summary>Whether the desktop named a piper program or a voices folder, usable or not.</summary>
    public bool Configured =>
        !string.IsNullOrWhiteSpace(options.Value.PiperPath) || !string.IsNullOrWhiteSpace(options.Value.VoicesPath);

    /// <summary>The bundled voices sorted by id, empty when piper or its voices are missing.</summary>
    public Task<IReadOnlyList<PiperVoice>> ProbeAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_lock)
        {
            if (_voices is { Count: > 0 } || (_voices is not null && time.GetUtcNow() - _probedAt < RetryAfter))
            {
                return Task.FromResult(_voices);
            }

            _voices = Find();
            _probedAt = time.GetUtcNow();
            return Task.FromResult(_voices);
        }
    }

    /// <summary>The voice narration uses when the owner chose none: the first Cori voice, else the first voice, else null.</summary>
    internal static PiperVoice? Default(IReadOnlyList<PiperVoice> voices)
        => voices.FirstOrDefault(v => Path.GetFileNameWithoutExtension(v.Model).Contains("cori", StringComparison.OrdinalIgnoreCase))
            ?? (voices.Count > 0 ? voices[0] : null);

    /// <summary>
    /// Make <paramref name="fileName"/> in <paramref name="directory"/> from <paramref name="text"/> in the bundled voice
    /// <paramref name="voiceId"/>, which must be one <see cref="ProbeAsync"/> lists; piper gets two minutes plus a second
    /// for every ten words, and an exit other than zero fails scene <paramref name="order"/> with piper's last error line.
    /// </summary>
    public async Task SynthesizeAsync(string voiceId, string text, int order, string directory, string fileName, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(text);
        var voices = await ProbeAsync(ct).ConfigureAwait(false);
        var voice = voices.FirstOrDefault(v => v.Id == voiceId) ?? throw new InvalidOperationException(UnknownVoice(voiceId));
        var words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
        var timeout = TimeSpan.FromMinutes(2) + TimeSpan.FromSeconds(words / 10);
        var failure = Failed(order);
        await MediaFiles.RenderAsync(
            directory,
            fileName,
            failure,
            async target =>
            {
                List<string> arguments = ["--model", voice.Model, "--output_file", target];
                if (voice.Speaker is { } speaker)
                {
                    arguments.AddRange(["--speaker", speaker.ToString(CultureInfo.InvariantCulture)]);
                }

                var result = await runner.RunAsync(options.Value.PiperPath.Trim(), arguments, timeout, ct, Spelled(text).ReplaceLineEndings(" "))
                    .ConfigureAwait(false);
                if (result.ExitCode != 0)
                {
                    throw new InvalidOperationException($"{failure} (piper: {FfmpegToolset.LastLine(result.ErrorTail)})");
                }
            },
            ct).ConfigureAwait(false);
    }

    private List<PiperVoice> Find()
    {
        var piper = options.Value.PiperPath.Trim();
        var folder = options.Value.VoicesPath.Trim();
        if (piper.Length == 0 || folder.Length == 0 || !File.Exists(piper) || !Directory.Exists(folder))
        {
            return [];
        }

        try
        {
            return [.. Directory.EnumerateFiles(folder, "*.onnx")
                .Where(model => Path.GetExtension(model) == ".onnx" && File.Exists(model + ".json"))
                .SelectMany(model => Read(model, model + ".json"))
                .OrderBy(v => v.Id, StringComparer.Ordinal)];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>
    /// Writes a run of two to four capital ASCII letters that touches no other letter or digit, with an optional lowercase
    /// plural s, as dotted letters, which the bundled voice spells in every position; a Roman numeral of I, V and X is left
    /// as it is, and so is any word written with lowercase or accented letters or with a digit.
    /// </summary>
    internal static string Spelled(string text)
        => Acronym().Replace(text, m => Numeral().IsMatch(m.Groups[1].Value)
            ? m.Value
            : string.Concat(m.Groups[1].Value.Select(letter => letter + ".")) + m.Groups[2].Value);

    [GeneratedRegex(@"(?<![\p{L}\p{N}])([A-Z]{2,4})(s?)(?![\p{L}\p{N}])")]
    private static partial Regex Acronym();

    [GeneratedRegex("^(X{0,3})(IX|IV|V?I{0,3})$")]
    private static partial Regex Numeral();

    private static List<PiperVoice> Read(string model, string config)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(config));
            var root = document.RootElement;
            var stem = Path.GetFileNameWithoutExtension(model);
            var dataset = Text(root, "dataset") ?? stem;
            var quality = root.TryGetProperty("audio", out var audio) && audio.ValueKind == JsonValueKind.Object ? Text(audio, "quality") : null;
            var name = CultureInfo.InvariantCulture.TextInfo.ToTitleCase(dataset.Replace('_', ' '));
            var suffix = string.IsNullOrEmpty(quality) ? string.Empty : $" ({quality})";
            var speakers = root.TryGetProperty("num_speakers", out var count) && count.ValueKind == JsonValueKind.Number && count.TryGetInt32(out var n) ? n : 1;
            if (speakers <= 1)
            {
                return [new PiperVoice($"{Prefix}{stem}", name + suffix, model, null)];
            }

            if (!root.TryGetProperty("speaker_id_map", out var map) || map.ValueKind != JsonValueKind.Object)
            {
                return [];
            }

            return [.. map.EnumerateObject()
                .Where(p => p.Value.ValueKind == JsonValueKind.Number && p.Value.TryGetInt32(out _))
                .Select(p => new PiperVoice($"{Prefix}{stem}#{p.Value.GetInt32()}", $"{name} {p.Name}{suffix}", model, p.Value.GetInt32()))];
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static string? Text(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
