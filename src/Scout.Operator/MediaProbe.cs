using System.Globalization;
using System.Text.RegularExpressions;

namespace Scout.Operator;

/// <summary>
/// Measures how long a media file in the project's media directory plays, from the <c>Duration:</c> line
/// <c>ffmpeg -i</c> writes about its input, and which streams it carries, from the <c>Stream</c> lines; ffmpeg exits with an error there because it was given no output, so its
/// exit code is not read.
/// </summary>
internal sealed partial class MediaProbe(IProcessRunner runner)
{
    internal static readonly TimeSpan Timeout = TimeSpan.FromMinutes(1);

    internal static string Unreadable(string file) => $"Scout could not read the length of {file}.";

    /// <summary>The length of <paramref name="file"/> in milliseconds; a file ffmpeg reports no length for fails the call.</summary>
    public async Task<int> DurationMsAsync(FfmpegTools tools, string directory, string file, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(tools);
        var path = MediaFiles.PathFor(directory, file);
        var result = await runner.RunAsync(tools.Ffmpeg, ["-hide_banner", "-nostdin", "-i", path], Timeout, ct).ConfigureAwait(false);
        return ParseDurationMs(result.ErrorTail) ?? throw new InvalidOperationException(Unreadable(file));
    }

    /// <summary>
    /// The length of <paramref name="file"/> in milliseconds and whether it has a picture stream, cover art aside, and a
    /// sound stream; a file ffmpeg reports no length for fails the call.
    /// </summary>
    public async Task<MediaStreams> InspectAsync(FfmpegTools tools, string directory, string file, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(tools);
        var path = MediaFiles.PathFor(directory, file);
        var result = await runner.RunAsync(tools.Ffmpeg, ["-hide_banner", "-nostdin", "-i", path], Timeout, ct).ConfigureAwait(false);
        var report = result.ErrorTail;
        var ms = ParseDurationMs(report) ?? throw new InvalidOperationException(Unreadable(file));
        return new MediaStreams(ms, PictureStream().IsMatch(report), SoundStream().IsMatch(report));
    }

    /// <summary>The <c>Duration: HH:MM:SS.xx</c> of ffmpeg's report on its input in milliseconds, or null when it has none.</summary>
    internal static int? ParseDurationMs(string report)
    {
        var match = DurationLine().Match(report);
        if (!match.Success)
        {
            return null;
        }

        var ms = ((int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) * 3600)
                + (int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture) * 60)
                + double.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture)) * 1000;
        return ms is > 0 and < int.MaxValue ? (int)Math.Round(ms) : null;
    }

    [GeneratedRegex(@"Duration: (\d{1,5}):(\d{2}):(\d{2}(?:\.\d+)?)")]
    private static partial Regex DurationLine();

    [GeneratedRegex(@"^\s*Stream #\d+:\d+\S*: Video: (?!.*\(attached pic\)).*$", RegexOptions.Multiline)]
    private static partial Regex PictureStream();

    [GeneratedRegex(@"^\s*Stream #\d+:\d+\S*: Audio: ", RegexOptions.Multiline)]
    private static partial Regex SoundStream();
}

/// <summary>What ffmpeg reports about a media file: its length and whether it carries a picture and a sound.</summary>
internal sealed record MediaStreams(int DurationMs, bool HasPicture, bool HasSound);
