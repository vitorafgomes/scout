namespace Scout.Operator;

/// <summary>Where a Short starts in the video, how long it lasts and the hook drawn at its top.</summary>
internal sealed record ShortPlan(int StartMs, int DurationMs, string Hook);

/// <summary>
/// Cuts up to <see cref="MaxShorts"/> vertical 1080x1920 Shorts from the assembled video, <c>short-1.mp4</c> and on. Each
/// starts where a scene starts and runs over whole following scenes while they fit in <see cref="MaxMs"/>, and is kept
/// only when it lasts at least <see cref="MinMs"/>; a single scene longer than that is cut at <see cref="MaxMs"/>. The
/// frame is the video fitted across the width over a copy of itself cropped to the vertical frame, shrunk, blurred with
/// <c>gblur</c> and scaled back up, with the lead scene's first sentence as the hook at the top.
/// </summary>
internal sealed class ShortsCutter(FfmpegToolset ffmpeg)
{
    internal const int MaxShorts = 3;

    internal const int MinMs = 20_000;

    internal const int MaxMs = 60_000;

    internal const int HookWidth = 22;

    internal static string CutFailed(int number) => $"Scout could not cut Short {number}";

    internal static string FileName(int number) => $"short-{number}.mp4";

    /// <summary>Cut the Shorts of <paramref name="video"/> and return them in order.</summary>
    public async Task<IReadOnlyList<OperatorShort>> CutAsync(FfmpegTools tools, string directory, AssembledVideo video, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(video);
        var source = MediaFiles.PathFor(directory, video.File);
        var shorts = new List<OperatorShort>();
        foreach (var (plan, number) in Plan(video.Cuts).Select((p, i) => (p, i + 1)))
        {
            var file = FileName(number);
            await MediaFiles.RenderAsync(
                directory,
                file,
                CutFailed(number),
                temp => ffmpeg.RunAsync(tools, Arguments(tools, source, plan, temp), plan.DurationMs, CutFailed(number), ct),
                ct).ConfigureAwait(false);
            shorts.Add(new OperatorShort(file, plan.StartMs, plan.DurationMs, plan.Hook));
        }

        return shorts;
    }

    internal static IReadOnlyList<ShortPlan> Plan(IReadOnlyList<SceneCut> cuts)
    {
        var plans = new List<ShortPlan>();
        var lead = 0;
        while (lead < cuts.Count && plans.Count < MaxShorts)
        {
            var end = lead;
            var length = 0;
            while (end < cuts.Count && length + cuts[end].DurationMs <= MaxMs)
            {
                length += cuts[end++].DurationMs;
            }

            if (end == lead)
            {
                length = MaxMs;
                end = lead + 1;
            }

            if (length >= MinMs)
            {
                plans.Add(new ShortPlan(cuts[lead].StartMs, length, FilterText.FirstSentence(cuts[lead].Scene.Narration)));
                lead = end;
            }
            else
            {
                lead++;
            }
        }

        return plans;
    }

    internal static List<string> Arguments(FfmpegTools tools, string video, ShortPlan plan, string output)
    {
        var hook = FilterText.Wrap(plan.Hook, HookWidth).Take(3)
            .Select((line, i) => FilterText.Draw(tools.FontPath, line, 72, 220 + (96 * i), boxed: true));
        var graph = "[0:v]split=2[back][front];"
            + "[back]crop=ih*9/16:ih,scale=270:480,gblur=sigma=6,scale=1080:1920[blurred];"
            + "[front]scale=1080:-2[fitted];"
            + $"[blurred][fitted]overlay=(W-w)/2:(H-h)/2,{string.Concat(hook.Select(h => h + ","))}setsar=1,format=yuv420p[v]";
        List<string> arguments =
        [
            "-hide_banner", "-nostdin", "-y",
            "-ss", FfmpegToolset.Seconds(plan.StartMs), "-t", FfmpegToolset.Seconds(plan.DurationMs), "-i", video,
            "-filter_complex", graph, "-map", "[v]", "-map", "0:a",
        ];
        arguments.AddRange(tools.VideoArguments);
        arguments.AddRange(tools.AudioArguments);
        arguments.AddRange(["-movflags", "+faststart", output]);
        return arguments;
    }
}
