using System.Globalization;
using System.Text.RegularExpressions;

namespace Scout.Operator;

/// <summary>
/// Measures how long a media file in the project's media directory plays, from the <c>Duration:</c> line
/// <c>ffmpeg -i</c> writes about its input; ffmpeg exits with an error there because it was given no output, so its
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
}
