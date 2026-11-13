using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Scout.Operator;

/// <summary>One mouth shape of a narration: when it starts and ends, in seconds, and its shape, <c>A</c> to <c>H</c> or <c>X</c>.</summary>
internal sealed record MouthCue(double Start, double End, string Shape);

/// <summary>The mouth cues of a narration, or the reason lip sync could not time them.</summary>
internal sealed record LipSync(IReadOnlyList<MouthCue>? Cues, string? Failure);

/// <summary>Whether lip sync can run on this machine, and a note for each thing that is missing.</summary>
internal sealed record LipSyncProbe(bool Available, IReadOnlyList<string> Notes);

/// <summary>
/// Times an animated character's mouth to its narration with the bundled <c>rhubarb</c> program at
/// <see cref="OperatorMediaOptions.RhubarbPath"/>, run through <see cref="IProcessRunner"/> with an argument list and
/// <see cref="TimeoutFor"/>, on its phonetic recognizer, which needs no language model. A narration that is not WAV or OGG is
/// first turned into a 16 kHz mono WAV with ffmpeg inside the media directory and removed after. Rhubarb writes the cues
/// as <c>mouth-cues-&lt;scene&gt;.json</c> in the media directory, read again while it is newer than the narration; a
/// failure, a timeout, a cancellation or cues that cannot be read leave no cues file. Nothing goes over the network.
/// </summary>
internal sealed class RhubarbLipSync(IProcessRunner runner, FfmpegToolset ffmpeg, IOptions<OperatorMediaOptions> options, TimeProvider time) : IDisposable
{
    /// <summary>Two minutes, plus two milliseconds per millisecond of narration.</summary>
    internal static TimeSpan TimeoutFor(int narrationMs) => TimeSpan.FromMinutes(2) + TimeSpan.FromMilliseconds(2L * Math.Max(0, narrationMs));

    internal static readonly string[] Shapes = ["A", "B", "C", "D", "E", "F", "G", "H", "X"];

    internal const string NoRhubarb = "Lip sync: the rhubarb program was not found.";

    internal const string NoMouths = "Lip sync: the mouth shapes were not found.";

    internal const string Unreadable = "Scout could not read the mouth cues rhubarb wrote.";

    internal const string ConvertFailed = "Scout could not prepare the narration for lip sync";

    internal static string Note(string failure) => $"Lip sync failed: {failure.Split('\n')[0].Trim()}";

    internal static string CuesFile(string sceneId) => $"mouth-cues-{sceneId}.json";

    private readonly SemaphoreSlim _once = new(1, 1);
    private bool? _answers;
    private DateTimeOffset _probedAt;

    public void Dispose()
    {
        _once.Dispose();
    }

    /// <summary>Whether the desktop named a rhubarb program or a mouths folder, usable or not.</summary>
    public bool Configured =>
        !string.IsNullOrWhiteSpace(options.Value.RhubarbPath) || !string.IsNullOrWhiteSpace(options.Value.MouthsPath);

    /// <summary>The folder of the nine mouth shapes.</summary>
    public string MouthsPath => options.Value.MouthsPath.Trim();

    /// <summary>
    /// Whether rhubarb answers <c>--version</c>, kept for the life of the process once it does and asked again after
    /// <see cref="FfmpegToolset.RetryAfter"/> when it does not, and whether the mouths folder holds the nine shapes.
    /// </summary>
    public async Task<LipSyncProbe> ProbeAsync(CancellationToken ct)
    {
        await _once.WaitAsync(ct).ConfigureAwait(false);
        bool answers;
        try
        {
            if (_answers is not true && (_answers is null || time.GetUtcNow() - _probedAt >= FfmpegToolset.RetryAfter))
            {
                _answers = await AnswersAsync(ct).ConfigureAwait(false);
                _probedAt = time.GetUtcNow();
            }

            answers = _answers.Value;
        }
        finally
        {
            _once.Release();
        }

        var mouths = MouthsPath.Length > 0 && Shapes.All(s => File.Exists(Path.Combine(MouthsPath, $"{s}.png")));
        List<string> notes = [.. answers ? [] : new[] { NoRhubarb }, .. mouths ? [] : new[] { NoMouths }];
        return new LipSyncProbe(answers && mouths, notes);
    }

    /// <summary>
    /// The mouth cues of <paramref name="audioFile"/>, the narration of scene <paramref name="sceneId"/>, which plays
    /// <paramref name="durationMs"/>; a name that would leave the media directory is refused before anything runs.
    /// </summary>
    public async Task<LipSync> CuesAsync(FfmpegTools tools, string directory, string sceneId, string audioFile, int durationMs, CancellationToken ct)
    {
        var audio = MediaFiles.PathFor(directory, audioFile);
        var cuesName = CuesFile(sceneId);
        var cues = MediaFiles.PathFor(directory, cuesName);
        if (File.Exists(cues) && File.GetLastWriteTimeUtc(cues) >= File.GetLastWriteTimeUtc(audio) && Read(cues) is { } kept)
        {
            return new LipSync(kept, null);
        }

        string? converted = null;
        try
        {
            var wav = audio;
            if (Path.GetExtension(audioFile).ToLowerInvariant() is not (".wav" or ".ogg"))
            {
                converted = $"lipsync-{sceneId}.wav";
                await MediaFiles.RenderAsync(
                    directory,
                    converted,
                    ConvertFailed,
                    temp => ffmpeg.RunAsync(
                        tools, ["-hide_banner", "-nostdin", "-y", "-i", audio, "-vn", "-ac", "1", "-ar", "16000", "-c:a", "pcm_s16le", temp], durationMs, ConvertFailed, ct),
                    ct).ConfigureAwait(false);
                wav = MediaFiles.PathFor(directory, converted);
            }

            var result = await runner.RunAsync(
                options.Value.RhubarbPath.Trim(), ["-f", "json", "-r", "phonetic", "--extendedShapes", "GHX", "-o", cues, wav], TimeoutFor(durationMs), ct)
                .ConfigureAwait(false);
            if (result.ExitCode != 0)
            {
                MediaFiles.Delete(directory, cuesName);
                return new LipSync(null, FfmpegToolset.LastLine(result.ErrorTail));
            }

            if (Read(cues) is { } read)
            {
                return new LipSync(read, null);
            }

            MediaFiles.Delete(directory, cuesName);
            return new LipSync(null, Unreadable);
        }
        catch (InvalidOperationException ex)
        {
            MediaFiles.Delete(directory, cuesName);
            return new LipSync(null, ex.Message);
        }
        catch (OperationCanceledException)
        {
            MediaFiles.Delete(directory, cuesName);
            throw;
        }
        finally
        {
            if (converted is not null)
            {
                MediaFiles.Delete(directory, converted);
            }
        }
    }

    /// <summary>The cues of rhubarb's JSON, each a known shape that ends after it starts; null when there are none or one is not.</summary>
    internal static List<MouthCue>? Parse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("mouthCues", out var list)
                || list.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var cues = new List<MouthCue>();
            foreach (var cue in list.EnumerateArray())
            {
                if (cue.ValueKind != JsonValueKind.Object
                    || Number(cue, "start") is not { } start
                    || Number(cue, "end") is not { } end
                    || !cue.TryGetProperty("value", out var value)
                    || value.ValueKind != JsonValueKind.String
                    || !Shapes.Contains(value.GetString(), StringComparer.Ordinal)
                    || start < 0
                    || end <= start)
                {
                    return null;
                }

                cues.Add(new MouthCue(start, end, value.GetString()!));
            }

            return cues.Count > 0 ? cues : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static List<MouthCue>? Read(string path)
    {
        try
        {
            return Parse(File.ReadAllText(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static double? Number(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) && double.IsFinite(number)
            ? number
            : null;

    private async Task<bool> AnswersAsync(CancellationToken ct)
    {
        var rhubarb = options.Value.RhubarbPath.Trim();
        if (rhubarb.Length == 0)
        {
            return false;
        }

        try
        {
            return (await runner.RunAsync(rhubarb, ["--version"], FfmpegToolset.ProbeTimeout, ct).ConfigureAwait(false)).ExitCode == 0;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }
}
