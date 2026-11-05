using System.Globalization;
using Microsoft.Extensions.Options;

namespace Scout.Operator;

/// <summary>
/// Where the operator finds <c>ffmpeg</c>, the font it draws captions and text cards with, and the bundled
/// <c>piper</c> and its voices, bound from <see cref="Section"/>. An empty ffmpeg path means the name on the system
/// path, then the Homebrew and <c>/usr/local</c> copies; an empty font path means no font; an empty piper or voices
/// path means no bundled voices.
/// </summary>
public sealed class OperatorMediaOptions
{
    /// <summary>The configuration section bound to these options.</summary>
    public const string Section = "Scout:Operator";

    /// <summary>The <c>ffmpeg</c> program; <c>ffmpeg</c> on the system path, then in the usual macOS folders, when empty.</summary>
    public string FfmpegPath { get; set; } = string.Empty;

    /// <summary>The font file captions and text cards are drawn with.</summary>
    public string FontPath { get; set; } = string.Empty;

    /// <summary>The bundled <c>piper</c> program that makes narration offline; no bundled voices when empty.</summary>
    public string PiperPath { get; set; } = string.Empty;

    /// <summary>The folder of the bundled voices, each a <c>&lt;id&gt;.onnx</c> model beside its <c>&lt;id&gt;.onnx.json</c>.</summary>
    public string VoicesPath { get; set; } = string.Empty;

    /// <summary>The bundled <c>rhubarb</c> program that times an animated character's mouth to its narration; no lip sync when empty.</summary>
    public string RhubarbPath { get; set; } = string.Empty;

    /// <summary>The folder of the nine mouth shapes, <c>A.png</c> to <c>H.png</c> and <c>X.png</c>; no lip sync when empty.</summary>
    public string MouthsPath { get; set; } = string.Empty;

    /// <summary>The YouTube category an upload is filed under; <c>22</c>, People &amp; Blogs, unless configured.</summary>
    public string CategoryId { get; set; } = "22";
}

/// <summary>The program, the font and the encoders an assembly uses.</summary>
internal sealed record FfmpegTools(string Ffmpeg, string FontPath, string VideoEncoder)
{
    internal const string Vp9 = "libvpx-vp9";

    /// <summary>
    /// The video encoding of every file the operator renders: the chosen encoder at 6 Mbit/s, yuv420p, 30 fps; VP9
    /// encodes at its real-time speed, on every row thread.
    /// </summary>
    public IReadOnlyList<string> VideoArguments => VideoEncoder == Vp9
        ? ["-c:v", Vp9, "-b:v", "6M", "-deadline", "realtime", "-cpu-used", "8", "-row-mt", "1", "-pix_fmt", "yuv420p", "-r", "30"]
        : ["-c:v", VideoEncoder, "-b:v", "6M", "-pix_fmt", "yuv420p", "-r", "30"];

    /// <summary>The audio encoding: AAC, or Opus beside VP9, at 192 kbit/s, 48 kHz, stereo.</summary>
    public IReadOnlyList<string> AudioArguments =>
        ["-c:a", VideoEncoder == Vp9 ? "libopus" : "aac", "-b:a", "192k", "-ar", "48000", "-ac", "2"];
}

/// <summary>Whether videos can be assembled: the tools when they can, and a note saying with what or why not.</summary>
internal sealed record FfmpegProbe(FfmpegTools? Tools, string Note);

/// <summary>
/// Finds the tools an assembly needs: <c>ffmpeg</c> must answer <c>-version</c>, the font file must exist,
/// <c>ffmpeg -encoders</c> must list <c>libopenh264</c>, else <c>h264_videotoolbox</c>, either beside <c>aac</c>, else
/// <c>libvpx-vp9</c> beside <c>libopus</c>, chosen in that order, <c>ffmpeg -filters</c> must list every filter in
/// <see cref="Filters"/> and <c>ffmpeg -demuxers</c> every demuxer in <see cref="Demuxers"/>. A probe that finds the tools
/// is kept for the life of the process; one that does not is kept for <see cref="RetryAfter"/>, then probed again.
/// </summary>
internal sealed class FfmpegToolset(IProcessRunner runner, IOptions<OperatorMediaOptions> options, TimeProvider time)
{
    internal static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(30);

    internal static readonly TimeSpan RetryAfter = TimeSpan.FromMinutes(2);

    internal static readonly string[] Candidates = ["ffmpeg", "/opt/homebrew/bin/ffmpeg", "/usr/local/bin/ffmpeg"];

    internal static readonly string[] Filters =
        ["anullsrc", "apad", "aresample", "color", "crop", "drawtext", "fade", "format", "gblur", "loudnorm", "overlay", "pad", "scale", "setsar", "split", "zoompan"];

    internal static readonly string[] Demuxers = ["concat", "lavfi"];

    internal const string NoFont = "Scout has no font for captions and text cards, so videos cannot be assembled.";

    internal const string NoEncoder =
        "The ffmpeg Scout found has no H.264 or VP9 encoder it can use, so videos cannot be assembled.";

    internal static string NotRunning(string tool) => $"Scout could not run {tool}, so videos cannot be assembled.";

    internal static string Ready(string encoder) => $"Videos are assembled with ffmpeg and the {encoder} encoder.";

    internal static string Missing(string name, string kind) =>
        $"The ffmpeg Scout found has no {name} {kind}, so videos cannot be assembled.";

    private readonly SemaphoreSlim _once = new(1, 1);
    private FfmpegProbe? _probe;
    private DateTimeOffset _probedAt;

    /// <summary>The tools, found once and kept; a failed probe is answered again for two minutes, then repeated.</summary>
    public async Task<FfmpegProbe> ProbeAsync(CancellationToken ct)
    {
        await _once.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_probe is { Tools: not null } || (_probe is not null && time.GetUtcNow() - _probedAt < RetryAfter))
            {
                return _probe;
            }

            _probe = await FindAsync(ct).ConfigureAwait(false);
            _probedAt = time.GetUtcNow();
            return _probe;
        }
        finally
        {
            _once.Release();
        }
    }

    /// <summary>
    /// Run ffmpeg with <paramref name="arguments"/>; an exit other than zero fails with <paramref name="failure"/> and the
    /// last line ffmpeg wrote to its error output.
    /// </summary>
    public async Task<ProcessResult> RunAsync(
        FfmpegTools tools, IReadOnlyList<string> arguments, int mediaMs, string failure, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(tools);
        var result = await runner.RunAsync(tools.Ffmpeg, arguments, TimeoutFor(tools, mediaMs), ct).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"{failure} (ffmpeg: {LastLine(result.ErrorTail)})");
        }

        return result;
    }

    /// <summary>Two minutes, plus four times the length of the media a command reads, or twelve times with VP9.</summary>
    internal static TimeSpan TimeoutFor(FfmpegTools tools, int mediaMs)
        => TimeSpan.FromMinutes(2) + TimeSpan.FromMilliseconds((tools.VideoEncoder == FfmpegTools.Vp9 ? 12L : 4L) * Math.Max(0, mediaMs));

    internal static string Seconds(int ms) => (ms / 1000.0).ToString("0.000", CultureInfo.InvariantCulture);

    internal static string LastLine(string text)
    {
        var line = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? "no detail";
        return line.Length > 200 ? line[..200] : line;
    }

    /// <summary>The encoder to use from the output of <c>ffmpeg -encoders</c>, or null when none of them is there.</summary>
    internal static string? PickEncoder(string encoders)
    {
        var names = encoders
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Where(parts => parts.Length >= 2 && parts[0].Length == 6 && parts[1] != "=")
            .Select(parts => parts[1])
            .ToHashSet(StringComparer.Ordinal);
        if (names.Contains("aac"))
        {
            foreach (var h264 in new[] { "libopenh264", "h264_videotoolbox" })
            {
                if (names.Contains(h264))
                {
                    return h264;
                }
            }
        }

        return names.Contains(FfmpegTools.Vp9) && names.Contains("libopus") ? FfmpegTools.Vp9 : null;
    }

    /// <summary>The first filter of <see cref="Filters"/> the output of <c>ffmpeg -filters</c> does not list, or null.</summary>
    internal static string? MissingFilter(string filters)
    {
        var names = filters
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Where(parts => parts.Length >= 3 && parts[1] != "=" && parts[2].Contains("->", StringComparison.Ordinal))
            .Select(parts => parts[1])
            .ToHashSet(StringComparer.Ordinal);
        return Filters.FirstOrDefault(f => !names.Contains(f));
    }

    /// <summary>The first demuxer of <see cref="Demuxers"/> the output of <c>ffmpeg -demuxers</c> does not list, or null.</summary>
    internal static string? MissingDemuxer(string demuxers)
    {
        var names = demuxers
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .SkipWhile(line => !line.StartsWith("--", StringComparison.Ordinal))
            .Skip(1)
            .SelectMany(line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(3))
            .ToHashSet(StringComparer.Ordinal);
        return Demuxers.FirstOrDefault(d => !names.Contains(d));
    }

    private async Task<FfmpegProbe> FindAsync(CancellationToken ct)
    {
        var settings = options.Value;
        string[] candidates = string.IsNullOrWhiteSpace(settings.FfmpegPath) ? Candidates : [settings.FfmpegPath.Trim()];
        string? ffmpeg = null;
        foreach (var candidate in candidates)
        {
            if (await AnswersAsync(candidate, ["-hide_banner", "-version"], ct).ConfigureAwait(false))
            {
                ffmpeg = candidate;
                break;
            }
        }

        if (ffmpeg is null)
        {
            return new FfmpegProbe(null, NotRunning("ffmpeg"));
        }

        if (string.IsNullOrWhiteSpace(settings.FontPath) || !File.Exists(settings.FontPath))
        {
            return new FfmpegProbe(null, NoFont);
        }

        var encoders = await ListAsync(ffmpeg, "-encoders", ct).ConfigureAwait(false);
        var encoder = encoders is null ? null : PickEncoder(encoders);
        if (encoder is null)
        {
            return new FfmpegProbe(null, encoders is null ? NotRunning("ffmpeg") : NoEncoder);
        }

        var filters = await ListAsync(ffmpeg, "-filters", ct).ConfigureAwait(false);
        if (filters is null)
        {
            return new FfmpegProbe(null, NotRunning("ffmpeg"));
        }

        if (MissingFilter(filters) is { } filter)
        {
            return new FfmpegProbe(null, Missing(filter, "filter"));
        }

        var demuxers = await ListAsync(ffmpeg, "-demuxers", ct).ConfigureAwait(false);
        if (demuxers is null)
        {
            return new FfmpegProbe(null, NotRunning("ffmpeg"));
        }

        return MissingDemuxer(demuxers) is { } demuxer
            ? new FfmpegProbe(null, Missing(demuxer, "demuxer"))
            : new FfmpegProbe(new FfmpegTools(ffmpeg, settings.FontPath, encoder), Ready(encoder));
    }

    private async Task<string?> ListAsync(string ffmpeg, string list, CancellationToken ct)
    {
        try
        {
            var listed = await runner.RunAsync(ffmpeg, ["-hide_banner", list], ProbeTimeout, ct).ConfigureAwait(false);
            return listed.ExitCode == 0 ? listed.Output : string.Empty;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private async Task<bool> AnswersAsync(string program, IReadOnlyList<string> arguments, CancellationToken ct)
    {
        try
        {
            return (await runner.RunAsync(program, arguments, ProbeTimeout, ct).ConfigureAwait(false)).ExitCode == 0;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }
}
