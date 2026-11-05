using System.Globalization;
using System.Text;

namespace Scout.Operator;

/// <summary>
/// The moving mouth of an animated character as an ffconcat list of the mouth shape pictures, one entry per run of equal
/// cues with its <c>duration</c> until the next run starts, the first from the start of the narration, and the last entry
/// written once more, because the concat demuxer ignores the duration of the last file; written in the media directory.
/// </summary>
internal static class MouthTrack
{
    internal const string VersionFile = "VERSION";

    internal static string FileName(string sceneId) => $"mouth-track-{sceneId}.txt";

    /// <summary>The ffconcat list of <paramref name="cues"/> over the shapes in <paramref name="mouths"/>.</summary>
    internal static string List(IReadOnlyList<MouthCue> cues, string mouths)
    {
        var runs = new List<MouthCue>();
        foreach (var cue in cues)
        {
            if (runs.Count > 0 && runs[^1].Shape == cue.Shape)
            {
                runs[^1] = runs[^1] with { End = cue.End };
            }
            else
            {
                runs.Add(cue);
            }
        }

        var list = new StringBuilder("ffconcat version 1.0\n");
        for (var i = 0; i < runs.Count; i++)
        {
            var start = i == 0 ? 0 : runs[i].Start;
            var end = i + 1 < runs.Count ? runs[i + 1].Start : runs[i].End;
            list.Append(CultureInfo.InvariantCulture, $"file {Quote(Shape(mouths, runs[i]))}\nduration {end - start:0.000}\n");
        }

        return runs.Count == 0 ? list.ToString() : list.Append(CultureInfo.InvariantCulture, $"file {Quote(Shape(mouths, runs[^1]))}\n").ToString();
    }

    /// <summary>Write the list of <paramref name="cues"/> for scene <paramref name="sceneId"/> and answer its path.</summary>
    internal static async Task<string> WriteAsync(string directory, string sceneId, IReadOnlyList<MouthCue> cues, string mouths, CancellationToken ct)
    {
        await MediaFiles.WriteAsync(directory, FileName(sceneId), Encoding.UTF8.GetBytes(List(cues, mouths)), ct).ConfigureAwait(false);
        return MediaFiles.PathFor(directory, FileName(sceneId));
    }

    /// <summary>The mouth set's version: the text of its <see cref="VersionFile"/>, else the folder's write time.</summary>
    internal static string Version(string mouths)
    {
        try
        {
            var marker = Path.Combine(mouths, VersionFile);
            return File.Exists(marker)
                ? File.ReadAllText(marker).Trim()
                : Directory.GetLastWriteTimeUtc(mouths).Ticks.ToString(CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }

    private static string Shape(string mouths, MouthCue cue) => Path.Combine(mouths, $"{cue.Shape}.png");

    private static string Quote(string path) => $"'{path.Replace("'", @"'\''", StringComparison.Ordinal)}'";
}
