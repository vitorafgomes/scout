using System.Security.Cryptography;
using System.Text;

namespace Scout.Operator;

/// <summary>One scene as it plays in the video: where it starts, how long it lasts, how many caption lines it shows and whether it is a placeholder.</summary>
internal sealed record SceneCut(OperatorScene Scene, int StartMs, int DurationMs, int Captions, bool Placeholder);

/// <summary>The assembled video file, its length, its scenes as they play and the segment files it was joined from.</summary>
internal sealed record AssembledVideo(string File, int DurationMs, IReadOnlyList<SceneCut> Cuts, IReadOnlyList<string> Segments);

/// <summary>
/// Renders one 1920x1080 segment per scene, in order, then joins them with ffmpeg's concat demuxer into
/// <see cref="VideoFile"/>. A scene shows its picture scaled and padded to the frame, held still, or, without one, a
/// text card with the first sentence of its narration on a dark background; a picture is read once a second and scaled
/// once a second, then repeated to 30 frames a second; it plays its narration, or silence, for its
/// duration, at least <see cref="MinSceneMs"/>; its narration is burned in as caption lines of at most
/// <see cref="CaptionWidth"/> characters, timed evenly across the scene. Every file is read and written inside the
/// project's media directory; a segment is named after its exact command and its inputs, so a segment already rendered
/// for the same inputs is used again.
/// </summary>
internal sealed class Assembler(FfmpegToolset ffmpeg)
{
    internal const string VideoFile = "video.mp4";

    internal const string ConcatFile = "concat.txt";

    internal const int MinSceneMs = 2000;

    internal const int CaptionWidth = 42;

    internal const int CardWidth = 32;

    internal const string JoinFailed = "Scout could not join the scenes into the video";

    internal static string RenderFailed(int order) => $"Scout could not render scene {order}";

    /// <summary>Render the video of <paramref name="scenes"/>, whose durations are the real lengths of their narration.</summary>
    public async Task<AssembledVideo> AssembleAsync(
        FfmpegTools tools, IReadOnlyList<OperatorScene> scenes, string directory, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(tools);
        ArgumentNullException.ThrowIfNull(scenes);
        var cuts = new List<SceneCut>();
        var segments = new List<string>();
        var start = 0;
        foreach (var scene in scenes.OrderBy(s => s.Order))
        {
            var duration = Math.Max(scene.DurationMs, MinSceneMs);
            var image = Usable(directory, scene.VisualFile);
            var audio = Usable(directory, scene.AudioFile);
            var captions = FilterText.Wrap(scene.Narration, CaptionWidth);
            var arguments = SegmentArguments(tools, scene, duration, image, audio, captions, string.Empty);
            var segment = $"segment-{Key(arguments, image, audio)}.mp4";
            if (!MediaFiles.Exists(directory, segment))
            {
                await MediaFiles.RenderAsync(
                    directory,
                    segment,
                    RenderFailed(scene.Order),
                    temp => ffmpeg.RunAsync(tools, [.. arguments.SkipLast(1), temp], duration, RenderFailed(scene.Order), ct),
                    ct).ConfigureAwait(false);
            }

            segments.Add(segment);
            cuts.Add(new SceneCut(scene, start, duration, captions.Count, IsPlaceholder(scene, image, audio)));
            start += duration;
        }

        await MediaFiles.WriteAsync(directory, ConcatFile, Encoding.UTF8.GetBytes(ConcatList(segments)), ct).ConfigureAwait(false);
        try
        {
            var list = MediaFiles.PathFor(directory, ConcatFile);
            await MediaFiles.RenderAsync(
                directory,
                VideoFile,
                JoinFailed,
                temp => ffmpeg.RunAsync(tools, ConcatArguments(tools, list, temp), start, JoinFailed, ct),
                ct).ConfigureAwait(false);
        }
        finally
        {
            MediaFiles.Delete(directory, ConcatFile);
        }

        return new AssembledVideo(VideoFile, start, cuts, segments);
    }

    internal static List<string> SegmentArguments(
        FfmpegTools tools, OperatorScene scene, int durationMs, string? image, string? audio, IReadOnlyList<string> captions, string output)
    {
        var seconds = FfmpegToolset.Seconds(durationMs);
        var arguments = new List<string> { "-hide_banner", "-nostdin", "-y" };
        var filters = new List<string>();
        if (image is not null)
        {
            arguments.AddRange(["-loop", "1", "-framerate", "1", "-t", seconds, "-i", image]);
            filters.AddRange(["scale=1920:1080:force_original_aspect_ratio=decrease", "pad=1920:1080:(ow-iw)/2:(oh-ih)/2:color=black", "setsar=1", "fps=30"]);
        }
        else
        {
            arguments.AddRange(["-f", "lavfi", "-t", seconds, "-i", "color=c=0x14181f:s=1920x1080:r=30"]);
            var lines = FilterText.Wrap(FilterText.FirstSentence(scene.Narration), CardWidth);
            filters.AddRange(lines.Select((line, i) =>
                FilterText.Draw(tools.FontPath, line, 64, ((1080 - (84 * lines.Count)) / 2) + (84 * i), boxed: false)));
        }

        arguments.AddRange(audio is not null
            ? ["-i", audio]
            : ["-f", "lavfi", "-t", seconds, "-i", "anullsrc=channel_layout=stereo:sample_rate=48000"]);
        filters.AddRange(captions.Select((line, i) => FilterText.Draw(
            tools.FontPath,
            line,
            44,
            940,
            boxed: true,
            (int)((long)durationMs * i / captions.Count),
            (int)((long)durationMs * (i + 1) / captions.Count))));
        filters.Add("format=yuv420p");
        arguments.AddRange([
            "-filter_complex", $"[0:v]{string.Join(",", filters)}[v];[1:a]aresample=48000,apad[a]",
            "-map", "[v]", "-map", "[a]", "-t", seconds,
        ]);
        arguments.AddRange(tools.VideoArguments);
        arguments.AddRange(tools.AudioArguments);
        arguments.Add(output);
        return arguments;
    }

    internal static List<string> ConcatArguments(FfmpegTools tools, string list, string output)
        => ["-hide_banner", "-nostdin", "-y", "-f", "concat", "-i", list, "-c:v", "copy", .. tools.AudioArguments, "-movflags", "+faststart", output];

    internal static string ConcatList(IEnumerable<string> segments)
        => "ffconcat version 1.0\n" + string.Concat(segments.Select(s => $"file '{s}'\n"));

    private static string? Usable(string directory, string? file)
    {
        if (file is null)
        {
            return null;
        }

        var path = MediaFiles.PathFor(directory, file);
        return File.Exists(path) ? path : null;
    }

    private static bool IsPlaceholder(OperatorScene scene, string? image, string? audio)
        => scene.Status != OperatorPipeline.Ready
            || (image is null && scene.VisualSource != OperatorPipeline.TextCard)
            || (audio is null && scene.AudioSource != OperatorPipeline.NoAudio);

    private static string Key(IEnumerable<string> arguments, string? image, string? audio)
    {
        var identity = new StringBuilder(string.Join('\n', arguments));
        foreach (var input in new[] { image, audio })
        {
            if (input is not null)
            {
                var file = new FileInfo(input);
                identity.Append('\n').Append(file.Length).Append('@').Append(file.LastWriteTimeUtc.Ticks);
            }
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity.ToString())))[..16];
    }
}
