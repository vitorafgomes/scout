using Scout.Operator.Tests.Fakes;

namespace Scout.Operator.Tests;

public sealed class ShortsCutterTests : IDisposable
{
    private readonly string _media = Directory.CreateTempSubdirectory("scout-shorts-").FullName;

    public void Dispose() => Directory.Delete(_media, recursive: true);

    private static List<SceneCut> Cuts(params int[] seconds)
    {
        var start = 0;
        return [.. seconds.Select((s, i) =>
        {
            var cut = new SceneCut(Given.Scene($"s{i + 1}", i + 1, $"Hook of scene {i + 1}. More words follow."), start, s * 1000, 1, false);
            start += s * 1000;
            return cut;
        })];
    }

    [Fact]
    public void At_Most_Three_Shorts_Start_On_Scene_Starts_Run_Over_Whole_Scenes_Up_To_60_Seconds_And_Last_At_Least_20()
    {
        ShortsCutter.Plan(Cuts(10, 15, 40, 5, 70, 30, 25)).ShouldBe([
            new ShortPlan(0, 25_000, "Hook of scene 1."),
            new ShortPlan(25_000, 45_000, "Hook of scene 3."),
            new ShortPlan(70_000, 60_000, "Hook of scene 5."),
        ]);
    }

    [Fact]
    public void A_Lead_Scene_That_Cannot_Reach_20_Seconds_Before_60_Is_Skipped_And_The_Next_Scene_Leads()
    {
        ShortsCutter.Plan(Cuts(15, 50, 10, 4)).ShouldBe([new ShortPlan(15_000, 60_000, "Hook of scene 2.")]);
        ShortsCutter.Plan(Cuts(5, 6, 7)).ShouldBeEmpty();
    }

    [Fact]
    public void A_Short_Is_1080x1920_With_Square_Pixels_The_Video_Fitted_Over_A_Copy_Cropped_Shrunk_And_Blurred_With_Gblur_With_The_Hook_At_The_Top()
    {
        var arguments = ShortsCutter.Arguments(
            Given.Tools(), "/media/p1/video.mp4", new ShortPlan(25_000, 45_000, "Why do rivers bend so much?"), "/media/p1/out.mp4");

        arguments.ShouldBe([
            "-hide_banner", "-nostdin", "-y", "-ss", "25.000", "-t", "45.000", "-i", "/media/p1/video.mp4",
            "-filter_complex",
            "[0:v]split=2[back][front];" +
            "[back]crop=ih*9/16:ih,scale=270:480,gblur=sigma=6,scale=1080:1920[blurred];" +
            "[front]scale=1080:-2[fitted];" +
            "[blurred][fitted]overlay=(W-w)/2:(H-h)/2," +
            "drawtext=fontfile=/fonts/NotoSans-Regular.ttf:expansion=none:text=Why do rivers bend so:fontsize=72:fontcolor=white:" +
            "x=(w-text_w)/2:y=220:box=1:boxcolor=black@0.6:boxborderw=16," +
            "drawtext=fontfile=/fonts/NotoSans-Regular.ttf:expansion=none:text=much?:fontsize=72:fontcolor=white:" +
            "x=(w-text_w)/2:y=316:box=1:boxcolor=black@0.6:boxborderw=16," +
            "setsar=1,format=yuv420p[v]",
            "-map", "[v]", "-map", "0:a",
            "-c:v", "libopenh264", "-b:v", "6M", "-pix_fmt", "yuv420p", "-r", "30",
            "-c:a", "aac", "-b:a", "192k", "-ar", "48000", "-ac", "2",
            "-movflags", "+faststart", "/media/p1/out.mp4",
        ]);
    }

    [Fact]
    public async Task Each_Short_Is_Saved_As_Short_N_Mp4_With_Where_It_Starts_Its_Length_And_Its_Hook()
    {
        File.WriteAllBytes(Path.Combine(_media, "video.mp4"), [1]);
        var runner = new FakeProcessRunner();
        var video = new AssembledVideo("video.mp4", 75_000, Cuts(25, 50), []);

        var shorts = await new ShortsCutter(Given.Toolset(runner)).CutAsync(Given.Tools(), _media, video, CancellationToken.None);

        shorts.ShouldBe([new OperatorShort("short-1.mp4", 0, 25_000, "Hook of scene 1."), new OperatorShort("short-2.mp4", 25_000, 50_000, "Hook of scene 2.")]);
        Directory.GetFiles(_media).Select(Path.GetFileName).Order().ShouldBe(["short-1.mp4", "short-2.mp4", "video.mp4"]);
        runner.Renders.ShouldAllBe(r => r.Contains(Path.Combine(_media, "video.mp4")));
    }
}
