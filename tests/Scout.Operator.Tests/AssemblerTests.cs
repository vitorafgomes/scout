using System.Text.RegularExpressions;
using Scout.Operator.Tests.Fakes;

namespace Scout.Operator.Tests;

public sealed class AssemblerTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("scout-assembler-").FullName;
    private readonly string _media;

    public AssemblerTests()
    {
        _media = Directory.CreateDirectory(Path.Combine(_root, "p1")).FullName;
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static Assembler Assembler(FakeProcessRunner runner) => new(Given.Toolset(runner));

    private string Media(string file) => Path.Combine(_media, file);

    private OperatorScene WithFiles(string id, int order, string narration, int durationMs)
    {
        File.WriteAllBytes(Media($"scene-{id}.png"), [1, 2]);
        File.WriteAllBytes(Media($"scene-{id}.mp3"), [3, 4]);
        return Given.Scene(id, order, narration, "ready", audioSource: "generated", visualSource: "generated",
            audioFile: $"scene-{id}.mp3", visualFile: $"scene-{id}.png", durationMs: durationMs);
    }

    [Fact]
    public void A_Scene_With_A_Picture_Reads_It_Once_A_Second_Scaled_And_Padded_To_1920x1080_Then_At_30_Fps_With_Its_Narration_And_Caption_And_No_Fast_Start()
    {
        var scene = Given.Scene("s1", 1, "Feed the starter daily.", "ready");

        var arguments = global::Scout.Operator.Assembler.SegmentArguments(
            Given.Tools(), scene, 3500, "/media/p1/scene-s1.png", "/media/p1/scene-s1.mp3", ["Feed the starter daily."], "/media/p1/out.mp4");

        arguments.ShouldBe([
            "-hide_banner", "-nostdin", "-y",
            "-loop", "1", "-framerate", "1", "-t", "3.500", "-i", "/media/p1/scene-s1.png",
            "-i", "/media/p1/scene-s1.mp3",
            "-filter_complex",
            "[0:v]scale=1920:1080:force_original_aspect_ratio=decrease,pad=1920:1080:(ow-iw)/2:(oh-ih)/2:color=black,setsar=1,fps=30," +
            "drawtext=fontfile=/fonts/NotoSans-Regular.ttf:expansion=none:text=Feed the starter daily.:fontsize=44:fontcolor=white:" +
            @"x=(w-text_w)/2:y=940:box=1:boxcolor=black@0.6:boxborderw=16:enable=between(t\,0.000\,3.500)," +
            "format=yuv420p[v];[1:a]aresample=48000,apad[a]",
            "-map", "[v]", "-map", "[a]", "-t", "3.500",
            "-c:v", "libopenh264", "-b:v", "6M", "-pix_fmt", "yuv420p", "-r", "30",
            "-c:a", "aac", "-b:a", "192k", "-ar", "48000", "-ac", "2",
            "/media/p1/out.mp4",
        ]);
    }

    [Fact]
    public void A_Scene_Without_A_Picture_Or_Narration_Is_A_Text_Card_Of_Its_First_Sentence_Over_Silence_With_No_Fast_Start()
    {
        var scene = Given.Scene("s2", 2, "Rivers bend. Water cuts the outer bank.");

        var arguments = global::Scout.Operator.Assembler.SegmentArguments(
            Given.Tools(), scene, 2000, null, null, ["Rivers bend. Water cuts the outer bank."], "/media/p1/out.mp4");

        arguments.ShouldBe([
            "-hide_banner", "-nostdin", "-y",
            "-f", "lavfi", "-t", "2.000", "-i", "color=c=0x14181f:s=1920x1080:r=30",
            "-f", "lavfi", "-t", "2.000", "-i", "anullsrc=channel_layout=stereo:sample_rate=48000",
            "-filter_complex",
            "[0:v]drawtext=fontfile=/fonts/NotoSans-Regular.ttf:expansion=none:text=Rivers bend.:fontsize=64:fontcolor=white:" +
            "x=(w-text_w)/2:y=498," +
            "drawtext=fontfile=/fonts/NotoSans-Regular.ttf:expansion=none:text=Rivers bend. Water cuts the outer bank.:fontsize=44:" +
            @"fontcolor=white:x=(w-text_w)/2:y=940:box=1:boxcolor=black@0.6:boxborderw=16:enable=between(t\,0.000\,2.000)," +
            "format=yuv420p[v];[1:a]aresample=48000,apad[a]",
            "-map", "[v]", "-map", "[a]", "-t", "2.000",
            "-c:v", "libopenh264", "-b:v", "6M", "-pix_fmt", "yuv420p", "-r", "30",
            "-c:a", "aac", "-b:a", "192k", "-ar", "48000", "-ac", "2",
            "/media/p1/out.mp4",
        ]);
    }

    [Fact]
    public void The_Concat_Demuxer_Joins_The_Segments_By_Plain_Name_Copying_The_Video_And_Re_Encoding_The_Audio_Into_A_Fast_Start_Mp4()
    {
        global::Scout.Operator.Assembler.ConcatList(["segment-aa.mp4", "segment-bb.mp4"])
            .ShouldBe("ffconcat version 1.0\nfile 'segment-aa.mp4'\nfile 'segment-bb.mp4'\n");
        global::Scout.Operator.Assembler.ConcatArguments(Given.Tools(), "/media/p1/concat.txt", "/media/p1/out.mp4").ShouldBe([
            "-hide_banner", "-nostdin", "-y", "-f", "concat", "-i", "/media/p1/concat.txt",
            "-c:v", "copy", "-c:a", "aac", "-b:a", "192k", "-ar", "48000", "-ac", "2",
            "-movflags", "+faststart", "/media/p1/out.mp4",
        ]);
    }

    [Fact]
    public void Text_And_Paths_Are_Escaped_For_The_Filter_Option_And_The_Filter_Graph_And_Control_Characters_Never_Reach_It()
    {
        FilterText.Escape(@"It's 10:30, [now]; a\b").ShouldBe(@"It\\\'s 10\\:30\, \[now\]\; a\\\\b");
        FilterText.Wrap("Line one\nwith a\ttab and\r\nmore words to wrap here", 16).ShouldBe(["Line one with a", "tab and more", "words to wrap", "here"]);
        FilterText.FirstSentence("  Why do rivers bend?  Because water cuts the outer bank.").ShouldBe("Why do rivers bend?");
    }

    [Fact]
    public async Task The_Video_Is_Assembled_From_One_Segment_Per_Scene_In_Order_With_Captions_Of_42_Characters_Timed_Evenly_Across_Each_Scene()
    {
        var runner = new FakeProcessRunner();
        var narration = "Rivers bend because the water runs faster on the outside of each curve and cuts that bank away.";
        var second = WithFiles("s2", 2, narration, 6000);
        var first = Given.Scene("s1", 1, "Why do rivers bend?", "ready", durationMs: 900);

        var video = await Assembler(runner).AssembleAsync(Given.Tools(), [second, first], _media, CancellationToken.None);

        video.File.ShouldBe("video.mp4");
        video.DurationMs.ShouldBe(8000);
        video.Cuts.Select(c => (c.Scene.Id, c.StartMs, c.DurationMs, c.Captions, c.Placeholder)).ShouldBe([
            ("s1", 0, 2000, 1, false), ("s2", 2000, 6000, 3, false),
        ]);
        var renders = runner.Renders;
        renders.Count.ShouldBe(3);
        renders[0].ShouldContain("color=c=0x14181f:s=1920x1080:r=30");
        renders[1].ShouldContain(Media("scene-s2.png"));
        Regex.Matches(renders[1].SkipWhile(a => a != "-filter_complex").ElementAt(1), @"text=([^:]+):fontsize=44.*?enable=between\(t\\,([\d.]+)\\,([\d.]+)\)")
            .Select(m => (m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value))
            .ShouldBe([
                ("Rivers bend because the water runs faster", "0.000", "2.000"),
                ("on the outside of each curve and cuts that", "2.000", "4.000"),
                ("bank away.", "4.000", "6.000"),
            ]);
        renders[2].Take(7).ShouldBe(["-hide_banner", "-nostdin", "-y", "-f", "concat", "-i", Media("concat.txt")]);
        Directory.GetFiles(_media).Select(Path.GetFileName).Where(f => !f!.StartsWith("segment-", StringComparison.Ordinal)).Order()
            .ShouldBe(["scene-s2.mp3", "scene-s2.png", "video.mp4"]);
        video.Segments.ShouldAllBe(s => File.Exists(Media(s)));
    }

    [Fact]
    public async Task A_Second_Assembly_Renders_Only_The_Segments_Whose_Command_Or_Input_Files_Changed()
    {
        var runner = new FakeProcessRunner();
        OperatorScene[] scenes = [WithFiles("s1", 1, "Feed it daily.", 3000), WithFiles("s2", 2, "Bake it hot.", 3000)];
        await Assembler(runner).AssembleAsync(Given.Tools(), scenes, _media, CancellationToken.None);
        File.WriteAllBytes(Media("scene-s2.png"), [9, 9, 9]);
        File.SetLastWriteTimeUtc(Media("scene-s2.png"), DateTime.UtcNow.AddMinutes(1));
        var again = new FakeProcessRunner();

        await Assembler(again).AssembleAsync(Given.Tools(), scenes, _media, CancellationToken.None);

        again.Renders.Count.ShouldBe(2);
        again.Renders[0].ShouldContain(Media("scene-s2.png"));
        again.Renders[1].ShouldContain("concat");
    }

    [Fact]
    public async Task A_Scene_File_That_Would_Leave_The_Media_Directory_Is_Refused_Before_Ffmpeg_Reads_Anything()
    {
        File.WriteAllBytes(Path.Combine(_root, "outside.png"), [1]);
        var runner = new FakeProcessRunner();
        var scene = Given.Scene("s1", 1, "Feed it.", "ready", visualSource: "owner", visualFile: "../outside.png", durationMs: 3000);

        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => Assembler(runner).AssembleAsync(Given.Tools(), [scene], _media, CancellationToken.None));

        ex.Message.ShouldBe("Scout refused to write a file outside the project's media folder.");
        runner.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_Segment_Ffmpeg_Fails_Stops_The_Assembly_With_The_Scene_And_Ffmpegs_Last_Line_And_Leaves_No_Temporary_File_Or_Video()
    {
        var runner = new FakeProcessRunner
        {
            Answer = (_, arguments) => arguments.Contains(Media("scene-s2.png"))
                ? new ProcessResult(1, string.Empty, "Stream mapping:\n[png @ 0x1] Invalid PNG signature 0x0902.\n")
                : null,
        };
        OperatorScene[] scenes = [WithFiles("s1", 1, "Feed it daily.", 3000), WithFiles("s2", 2, "Bake it hot.", 3000)];

        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => Assembler(runner).AssembleAsync(Given.Tools(), scenes, _media, CancellationToken.None));

        ex.Message.ShouldBe("Scout could not render scene 2 (ffmpeg: [png @ 0x1] Invalid PNG signature 0x0902.)");
        var left = Directory.GetFiles(_media).Select(Path.GetFileName).ToList();
        left.ShouldNotContain(f => f!.StartsWith('.'));
        left.ShouldNotContain("video.mp4");
        left.ShouldNotContain("concat.txt");
        left.Count(f => f!.StartsWith("segment-", StringComparison.Ordinal)).ShouldBe(1);
    }

    [Fact]
    public async Task A_Join_That_Exits_Zero_But_Writes_Under_1_KB_Is_Refused_With_The_Step_And_Leaves_The_Previous_Video_Whole()
    {
        byte[] previous = [.. Enumerable.Repeat((byte)7, 2048)];
        File.WriteAllBytes(Media("video.mp4"), previous);
        var runner = new FakeProcessRunner
        {
            Answer = (_, arguments) =>
            {
                if (!arguments.Contains("concat"))
                {
                    return null;
                }

                File.WriteAllBytes(arguments[^1], new byte[261]);
                return new ProcessResult(0, string.Empty, string.Empty);
            },
        };

        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => Assembler(runner).AssembleAsync(Given.Tools(), [WithFiles("s1", 1, "Feed it daily.", 3000)], _media, CancellationToken.None));

        ex.Message.ShouldBe("Scout could not join the scenes into the video (ffmpeg wrote an empty file)");
        File.ReadAllBytes(Media("video.mp4")).ShouldBe(previous);
        var left = Directory.GetFiles(_media).Select(Path.GetFileName).ToList();
        left.ShouldNotContain(f => f!.StartsWith('.'));
        left.ShouldNotContain("concat.txt");
    }

    [Fact]
    public async Task A_Scene_That_Is_Not_Ready_Or_Lost_Its_Picture_Is_Assembled_As_A_Text_Card_And_Marked_A_Placeholder()
    {
        var runner = new FakeProcessRunner();
        OperatorScene[] scenes =
        [
            Given.Scene("s1", 1, "Planned only."),
            Given.Scene("s2", 2, "Picture gone.", "ready", visualSource: "generated", visualFile: "scene-s2.png", durationMs: 3000),
            Given.Scene("s3", 3, "A text card.", "ready", durationMs: 3000),
        ];

        var video = await Assembler(runner).AssembleAsync(Given.Tools(), scenes, _media, CancellationToken.None);

        video.Cuts.Select(c => c.Placeholder).ShouldBe([true, true, false]);
        runner.Renders.Take(3).ShouldAllBe(r => r.Contains("color=c=0x14181f:s=1920x1080:r=30"));
    }
}
