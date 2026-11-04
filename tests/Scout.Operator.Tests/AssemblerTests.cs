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
    public void A_picture_scene_is_rendered_with_a_slow_zoom_and_fades()
    {
        var scene = Given.Scene("s1", 1, "Feed the starter daily.", "ready");

        var first = global::Scout.Operator.Assembler.SegmentArguments(
            Given.Tools(), scene, 0, 3500, "/media/p1/scene-s1.png", "/media/p1/scene-s1.mp3", ["Feed the starter daily."], "gentle", "/media/p1/out.mp4");
        var second = global::Scout.Operator.Assembler.SegmentArguments(
            Given.Tools(), scene, 1, 3500, "/media/p1/scene-s1.png", "/media/p1/scene-s1.mp3", ["Feed the starter daily."], "gentle", "/media/p1/out.mp4");

        first.ShouldBe([
            "-hide_banner", "-nostdin", "-y",
            "-i", "/media/p1/scene-s1.png",
            "-i", "/media/p1/scene-s1.mp3",
            "-filter_complex",
            "[0:v]scale=3840:2160:force_original_aspect_ratio=decrease,pad=3840:2160:(ow-iw)/2:(oh-ih)/2:color=black,setsar=1," +
            "zoompan=z=1+0.08*on/104:x=(iw-iw/zoom)*(0.25+0.25*on/104):y=(ih-ih/zoom)*(0.25+0.25*on/104):d=105:s=1920x1080:fps=30," +
            "drawtext=fontfile=/fonts/NotoSans-Regular.ttf:expansion=none:text=Feed the starter daily.:fontsize=44:fontcolor=white:" +
            @"x=(w-text_w)/2:y=940:box=1:boxcolor=black@0.6:boxborderw=16:enable=between(t\,0.000\,3.500)," +
            "fade=t=in:st=0:d=0.4,fade=t=out:st=3.100:d=0.4," +
            "format=yuv420p[v];[1:a]aresample=48000,apad[a]",
            "-map", "[v]", "-map", "[a]", "-t", "3.500",
            "-c:v", "libopenh264", "-b:v", "6M", "-pix_fmt", "yuv420p", "-r", "30",
            "-c:a", "aac", "-b:a", "192k", "-ar", "48000", "-ac", "2",
            "/media/p1/out.mp4",
        ]);
        Graph(second).ShouldBe(
            "[0:v]scale=3840:2160:force_original_aspect_ratio=decrease,pad=3840:2160:(ow-iw)/2:(oh-ih)/2:color=black,setsar=1," +
            "zoompan=z=1.08-0.08*on/104:x=(iw-iw/zoom)/2:y=(ih-ih/zoom)/2:d=105:s=1920x1080:fps=30," +
            "drawtext=fontfile=/fonts/NotoSans-Regular.ttf:expansion=none:text=Feed the starter daily.:fontsize=44:fontcolor=white:" +
            @"x=(w-text_w)/2:y=940:box=1:boxcolor=black@0.6:boxborderw=16:enable=between(t\,0.000\,3.500)," +
            "fade=t=in:st=0:d=0.4,fade=t=out:st=3.100:d=0.4," +
            "format=yuv420p[v];[1:a]aresample=48000,apad[a]");
    }

    [Fact]
    public void Still_gentle_and_dynamic_build_their_own_graphs()
    {
        var scene = Given.Scene("s1", 1, "Rivers bend. Water cuts the outer bank.", "ready");
        List<string> Picture(string motion, int position) => global::Scout.Operator.Assembler.SegmentArguments(
            Given.Tools(), scene, position, 3500, "/media/p1/scene-s1.png", "/media/p1/scene-s1.mp3", ["Rivers bend."], motion, "/media/p1/out.mp4");
        string Card(string motion) => Graph(global::Scout.Operator.Assembler.SegmentArguments(
            Given.Tools(), scene, 0, 3500, null, null, ["Rivers bend."], motion, "/media/p1/out.mp4"));

        var still = Picture("still", 0);

        still.Take(11).ShouldBe(["-hide_banner", "-nostdin", "-y", "-loop", "1", "-framerate", "1", "-t", "3.500", "-i", "/media/p1/scene-s1.png"]);
        Graph(still).ShouldBe(
            "[0:v]scale=1920:1080:force_original_aspect_ratio=decrease,pad=1920:1080:(ow-iw)/2:(oh-ih)/2:color=black,setsar=1,fps=30," +
            "drawtext=fontfile=/fonts/NotoSans-Regular.ttf:expansion=none:text=Rivers bend.:fontsize=44:fontcolor=white:" +
            @"x=(w-text_w)/2:y=940:box=1:boxcolor=black@0.6:boxborderw=16:enable=between(t\,0.000\,3.500)," +
            "fade=t=in:st=0:d=0.4,fade=t=out:st=3.100:d=0.4," +
            "format=yuv420p[v];[1:a]aresample=48000,apad[a]");
        Graph(Picture("still", 1)).ShouldNotContain("zoompan", Case.Sensitive);
        Graph(Picture("gentle", 0)).ShouldContain(
            "zoompan=z=1+0.08*on/104:x=(iw-iw/zoom)*(0.25+0.25*on/104):y=(ih-ih/zoom)*(0.25+0.25*on/104):d=105:s=1920x1080:fps=30,",
            Case.Sensitive);
        Graph(Picture("gentle", 1)).ShouldContain("zoompan=z=1.08-0.08*on/104:x=(iw-iw/zoom)/2:y=(ih-ih/zoom)/2:d=105:s=1920x1080:fps=30,", Case.Sensitive);
        Graph(Picture("dynamic", 0)).ShouldContain(
            "zoompan=z=1+0.16*on/104:x=(iw-iw/zoom)*(0.25+0.5*on/104):y=(ih-ih/zoom)*(0.25+0.5*on/104):d=105:s=1920x1080:fps=30,",
            Case.Sensitive);
        Graph(Picture("dynamic", 1)).ShouldContain("zoompan=z=1.16-0.16*on/104:x=(iw-iw/zoom)/2:y=(ih-ih/zoom)/2:d=105:s=1920x1080:fps=30,", Case.Sensitive);
        Picture("spin", 0).ShouldBe(Picture("gentle", 0));
        Card("still").ShouldContain(@"y=498+24*(1-min(t/0.6\,1))", Case.Sensitive);
        Card("gentle").ShouldContain(@"y=498+24*(1-min(t/0.6\,1))", Case.Sensitive);
        Card("dynamic").ShouldContain(@"y=498+48*(1-min(t/0.6\,1))", Case.Sensitive);
        Card("dynamic").ShouldNotContain(@"y=498+24*", Case.Sensitive);
    }

    [Fact]
    public void A_text_card_scene_fades_and_slides_its_sentence_in()
    {
        var scene = Given.Scene("s2", 2, "Rivers bend. Water cuts the outer bank.");

        var arguments = global::Scout.Operator.Assembler.SegmentArguments(
            Given.Tools(), scene, 1, 2000, null, null, ["Rivers bend. Water cuts the outer bank."], "gentle", "/media/p1/out.mp4");

        arguments.ShouldBe([
            "-hide_banner", "-nostdin", "-y",
            "-f", "lavfi", "-t", "2.000", "-i", "color=c=0x14181f:s=1920x1080:r=30",
            "-f", "lavfi", "-t", "2.000", "-i", "anullsrc=channel_layout=stereo:sample_rate=48000",
            "-filter_complex",
            "[0:v]drawtext=fontfile=/fonts/NotoSans-Regular.ttf:expansion=none:text=Rivers bend.:fontsize=64:fontcolor=white:" +
            @"x=(w-text_w)/2:y=498+24*(1-min(t/0.6\,1)):alpha=if(lt(t\,0.6)\,t/0.6\,1)," +
            "drawtext=fontfile=/fonts/NotoSans-Regular.ttf:expansion=none:text=Rivers bend. Water cuts the outer bank.:fontsize=44:" +
            @"fontcolor=white:x=(w-text_w)/2:y=940:box=1:boxcolor=black@0.6:boxborderw=16:enable=between(t\,0.000\,2.000)," +
            "fade=t=out:st=1.600:d=0.4," +
            "format=yuv420p[v];[1:a]aresample=48000,apad[a]",
            "-map", "[v]", "-map", "[a]", "-t", "2.000",
            "-c:v", "libopenh264", "-b:v", "6M", "-pix_fmt", "yuv420p", "-r", "30",
            "-c:a", "aac", "-b:a", "192k", "-ar", "48000", "-ac", "2",
            "/media/p1/out.mp4",
        ]);
    }

    [Theory]
    [InlineData(1999, "/media/p1/scene-s1.png", new[] { "fade=t=in:st=0:d=0.4" })]
    [InlineData(2000, "/media/p1/scene-s1.png", new[] { "fade=t=in:st=0:d=0.4", "fade=t=out:st=1.600:d=0.4" })]
    [InlineData(1999, null, new string[0])]
    [InlineData(2000, null, new[] { "fade=t=out:st=1.600:d=0.4" })]
    public void A_scene_under_two_seconds_has_no_fade_out(int durationMs, string? image, string[] fades)
    {
        var arguments = global::Scout.Operator.Assembler.SegmentArguments(
            Given.Tools(), Given.Scene("s1", 1, "Feed it."), 0, durationMs, image, null, ["Feed it."], "gentle", "/media/p1/out.mp4");

        Regex.Matches(Graph(arguments), @"fade=t=\w+:st=[\d.]+:d=0\.4").Select(m => m.Value).ShouldBe(fades);
    }

    [Fact]
    public void A_font_path_with_quotes_and_colons_is_escaped_in_the_text_card_graph()
    {
        var tools = new FfmpegTools("/opt/scout/ffmpeg", "/fonts/It's a:font.ttf", "libopenh264");
        var scene = Given.Scene("s1", 1, "Feed the starter daily.");

        var arguments = global::Scout.Operator.Assembler.SegmentArguments(
            tools, scene, 0, 3000, null, null, ["Feed the starter daily."], "gentle", "/media/p1/out.mp4");

        var graph = Graph(arguments);
        graph.ShouldNotContain("fontfile=/fonts/It's", Case.Sensitive);
        Regex.Matches(graph, Regex.Escape(@"fontfile=/fonts/It\\\'s a\\:font.ttf:expansion=none")).Count.ShouldBe(2);
    }

    [Fact]
    public void A_narration_with_quotes_colons_percent_and_backslashes_is_escaped_inside_the_motion_graph()
    {
        const string narration = @"It's 10:30, 50% off a\b. Then more.";
        var scene = Given.Scene("s1", 1, narration);

        var card = global::Scout.Operator.Assembler.SegmentArguments(
            Given.Tools(), scene, 0, 3000, null, null, [narration], "gentle", "/media/p1/out.mp4");
        var picture = global::Scout.Operator.Assembler.SegmentArguments(
            Given.Tools(), scene, 0, 3000, "/media/p1/scene-s1.png", null, [narration], "gentle", "/media/p1/out.mp4");

        Graph(card).ShouldBe(
            @"[0:v]drawtext=fontfile=/fonts/NotoSans-Regular.ttf:expansion=none:text=It\\\'s 10\\:30\, 50% off a\\\\b.:fontsize=64:fontcolor=white:" +
            @"x=(w-text_w)/2:y=498+24*(1-min(t/0.6\,1)):alpha=if(lt(t\,0.6)\,t/0.6\,1)," +
            @"drawtext=fontfile=/fonts/NotoSans-Regular.ttf:expansion=none:text=It\\\'s 10\\:30\, 50% off a\\\\b. Then more.:fontsize=44:" +
            @"fontcolor=white:x=(w-text_w)/2:y=940:box=1:boxcolor=black@0.6:boxborderw=16:enable=between(t\,0.000\,3.000)," +
            "fade=t=out:st=2.600:d=0.4," +
            "format=yuv420p[v];[1:a]aresample=48000,apad[a]");
        Graph(picture).ShouldContain(
            "d=90:s=1920x1080:fps=30," +
            @"drawtext=fontfile=/fonts/NotoSans-Regular.ttf:expansion=none:text=It\\\'s 10\\:30\, 50% off a\\\\b. Then more.:fontsize=44:",
            Case.Sensitive);
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

        var video = await Assembler(runner).AssembleAsync(Given.Tools(), [second, first], _media, null, null, "gentle", CancellationToken.None);

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
        await Assembler(runner).AssembleAsync(Given.Tools(), scenes, _media, null, null, "gentle", CancellationToken.None);
        File.WriteAllBytes(Media("scene-s2.png"), [9, 9, 9]);
        File.SetLastWriteTimeUtc(Media("scene-s2.png"), DateTime.UtcNow.AddMinutes(1));
        var again = new FakeProcessRunner();

        await Assembler(again).AssembleAsync(Given.Tools(), scenes, _media, null, null, "gentle", CancellationToken.None);

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
            () => Assembler(runner).AssembleAsync(Given.Tools(), [scene], _media, null, null, "gentle", CancellationToken.None));

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
            () => Assembler(runner).AssembleAsync(Given.Tools(), scenes, _media, null, null, "gentle", CancellationToken.None));

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
            () => Assembler(runner).AssembleAsync(Given.Tools(), [WithFiles("s1", 1, "Feed it daily.", 3000)], _media, null, null, "gentle", CancellationToken.None));

        ex.Message.ShouldBe("Scout could not join the scenes into the video (the file came out empty)");
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

        var video = await Assembler(runner).AssembleAsync(Given.Tools(), scenes, _media, null, null, "gentle", CancellationToken.None);

        video.Cuts.Select(c => c.Placeholder).ShouldBe([true, true, false]);
        runner.Renders.Take(3).ShouldAllBe(r => r.Contains("color=c=0x14181f:s=1920x1080:r=30"));
    }

    private static string Graph(IReadOnlyList<string> render) => render.SkipWhile(a => a != "-filter_complex").ElementAt(1);

    [Fact]
    public async Task A_scene_without_a_picture_uses_the_project_background()
    {
        File.WriteAllBytes(Media("studio.png"), [5, 6]);
        var runner = new FakeProcessRunner();
        OperatorScene[] scenes =
        [
            Given.Scene("s1", 1, "A text card. With two sentences.", "ready", durationMs: 3000),
            Given.Scene("s2", 2, "Picture gone.", "ready", visualSource: "generated", visualFile: "scene-s2.png", durationMs: 3000),
            WithFiles("s3", 3, "My own picture.", 3000),
        ];

        await Assembler(runner).AssembleAsync(Given.Tools(), scenes, _media, "studio.png", null, "gentle", CancellationToken.None);

        var renders = runner.Renders;
        renders.Count.ShouldBe(4);
        foreach (var (render, caption, zoom) in new[] { (renders[0], "A text card. With two sentences.", "z=1+0.08*on/89"), (renders[1], "Picture gone.", "z=1.08-0.08*on/89") })
        {
            render.SkipWhile(a => a != "-i").Take(2).ShouldBe(["-i", Media("studio.png")]);
            Graph(render).ShouldStartWith(
                $"[0:v]scale=3840:2160:force_original_aspect_ratio=decrease,pad=3840:2160:(ow-iw)/2:(oh-ih)/2:color=black,setsar=1,zoompan={zoom}:");
            Graph(render).ShouldContain(
                $"d=90:s=1920x1080:fps=30,drawtext=fontfile=/fonts/NotoSans-Regular.ttf:expansion=none:text={caption}:fontsize=44:", Case.Sensitive);
            Graph(render).ShouldNotContain("fontsize=64");
            render.ShouldNotContain("color=c=0x14181f:s=1920x1080:r=30");
        }

        renders[2].ShouldContain(Media("scene-s3.png"));
        renders[2].ShouldNotContain(Media("studio.png"));
    }

    [Fact]
    public async Task A_background_outside_the_media_folder_fails_the_project()
    {
        File.WriteAllBytes(Path.Combine(_root, "outside.png"), [1]);
        var runner = new FakeProcessRunner();
        OperatorScene[] scenes = [WithFiles("s1", 1, "My own picture.", 3000), Given.Scene("s2", 2, "A text card.", "ready", durationMs: 3000)];

        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => Assembler(runner).AssembleAsync(Given.Tools(), scenes, _media, "../outside.png", null, "gentle", CancellationToken.None));

        ex.Message.ShouldBe("Scout refused to write a file outside the project's media folder.");
        runner.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_changed_background_changes_the_segment_name()
    {
        File.WriteAllBytes(Media("studio.png"), [5, 6]);
        OperatorScene[] scenes = [Given.Scene("s1", 1, "A text card.", "ready", durationMs: 3000)];
        var first = await Assembler(new FakeProcessRunner()).AssembleAsync(Given.Tools(), scenes, _media, "studio.png", null, "gentle", CancellationToken.None);
        File.WriteAllBytes(Media("studio.png"), [7, 8, 9]);
        File.SetLastWriteTimeUtc(Media("studio.png"), new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var again = new FakeProcessRunner();

        var second = await Assembler(again).AssembleAsync(Given.Tools(), scenes, _media, "studio.png", null, "gentle", CancellationToken.None);

        second.Segments.ShouldHaveSingleItem().ShouldNotBe(first.Segments.ShouldHaveSingleItem());
        again.Renders.Count.ShouldBe(2);
        again.Renders[0].ShouldContain(Media("studio.png"));
    }

    [Fact]
    public async Task A_changed_motion_changes_the_segment_name()
    {
        OperatorScene[] scenes = [WithFiles("s1", 1, "Feed it daily.", 3000)];

        var names = new List<string>();
        foreach (var motion in new[] { "gentle", "still", "dynamic" })
        {
            var video = await Assembler(new FakeProcessRunner()).AssembleAsync(Given.Tools(), scenes, _media, null, null, motion, CancellationToken.None);
            names.Add(video.Segments.ShouldHaveSingleItem());
        }

        names.Distinct(StringComparer.Ordinal).Count().ShouldBe(3);
    }

    [Fact]
    public async Task The_brief_is_not_part_of_the_segment_command()
    {
        const string brief = "Zebra'stripes: [neon]; quokka\nglow,pinkish";
        File.WriteAllBytes(Media("scene-s1.png"), [1, 2]);
        File.WriteAllBytes(Media("scene-s1.mp3"), [3, 4]);
        var project = Given.Producing(
            Given.Scene("s1", 1, "Feed it daily.", "ready", audioSource: "generated", visualSource: "generated",
                audioFile: "scene-s1.mp3", visualFile: "scene-s1.png", durationMs: 3000),
            Given.Scene("s2", 2, "Bake it hot.", "ready", durationMs: 3000)) with
        {
            Status = "assembling",
            Direction = brief,
            Motion = "dynamic",
        };
        var runner = new FakeProcessRunner { ProbedMs = 3000 };
        var stage = Given.Assembly(runner, new VisualGenerator(Given.Media(images: Given.Images()), Given.Capabilities(Given.Media(images: Given.Images())), new Microsoft.Extensions.Time.Testing.FakeTimeProvider(Given.Now)));

        await stage.RenderAsync(Given.Tools(), project, _media, CancellationToken.None);

        runner.Renders.Count.ShouldBeGreaterThanOrEqualTo(3);
        Graph(runner.Renders[0]).ShouldContain("zoompan=z=1+0.16*", Case.Sensitive);
        var arguments = runner.Calls.SelectMany(c => c.Arguments).ToList();
        foreach (var fragment in new[] { "Zebra", "stripes", "neon", "quokka", "glow", "pinkish" })
        {
            arguments.ShouldNotContain(a => a.Contains(fragment, StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public async Task A_background_the_media_folder_does_not_hold_leaves_the_dark_text_card()
    {
        var runner = new FakeProcessRunner();

        await Assembler(runner).AssembleAsync(
            Given.Tools(), [Given.Scene("s1", 1, "A text card.", "ready", durationMs: 3000)], _media, "studio.png", null, "gentle", CancellationToken.None);

        runner.Renders[0].ShouldContain("color=c=0x14181f:s=1920x1080:r=30");
        Graph(runner.Renders[0]).ShouldContain("fontsize=64");
    }

    private static OperatorScene Clip(string id, int order, string narration, int startMs, int endMs)
    {
        var scene = Given.Scene(id, order, narration, "ready", audioSource: "recording", visualSource: "recording", durationMs: endMs - startMs);
        return scene with { VisualPrompt = string.Empty, ClipStartMs = startMs, ClipEndMs = endMs };
    }

    private string Recorded()
    {
        File.WriteAllBytes(Media("owner-recording-1a.mp4"), [1, 2, 3, 4]);
        return "owner-recording-1a.mp4";
    }

    [Fact]
    public async Task A_Recording_Scene_Trims_The_Recording_To_Its_Clip_Scaled_To_1080p30_With_Its_Own_Sound_And_Captions()
    {
        var middle = global::Scout.Operator.Assembler.ClipArguments(
            Given.Tools(), Clip("s2", 2, "Feed the starter daily.", 23000, 75200), first: false, last: false,
            "/media/p1/owner-recording-1a.mp4", ["Feed the starter daily."], "/media/p1/out.mp4");
        var runner = new FakeProcessRunner();
        var recording = Recorded();

        var video = await Assembler(runner).AssembleAsync(
            Given.Tools(), [Clip("s1", 1, "Hello.", 0, 1500), Clip("s2", 2, "Bye.", 1500, 4000)], _media, null, recording, "dynamic", CancellationToken.None);

        middle.ShouldBe([
            "-hide_banner", "-nostdin", "-y",
            "-ss", "23.000", "-t", "52.200", "-i", "/media/p1/owner-recording-1a.mp4",
            "-filter_complex",
            "[0:v]scale=1920:1080:force_original_aspect_ratio=decrease,pad=1920:1080:(ow-iw)/2:(oh-ih)/2:color=black,setsar=1,fps=30," +
            "drawtext=fontfile=/fonts/NotoSans-Regular.ttf:expansion=none:text=Feed the starter daily.:fontsize=44:fontcolor=white:" +
            @"x=(w-text_w)/2:y=940:box=1:boxcolor=black@0.6:boxborderw=16:enable=between(t\,0.000\,52.200)," +
            "format=yuv420p[v];[0:a]aresample=48000,apad[a]",
            "-map", "[v]", "-map", "[a]", "-t", "52.200",
            "-c:v", "libopenh264", "-b:v", "6M", "-pix_fmt", "yuv420p", "-r", "30",
            "-c:a", "aac", "-b:a", "192k", "-ar", "48000", "-ac", "2",
            "/media/p1/out.mp4",
        ]);
        video.DurationMs.ShouldBe(4000);
        video.Cuts.Select(c => (c.Scene.Id, c.StartMs, c.DurationMs, c.Captions, c.Placeholder)).ShouldBe([
            ("s1", 0, 1500, 1, false), ("s2", 1500, 2500, 1, false),
        ]);
        runner.Renders.Take(2).Select(r => r.Take(9)).ShouldBe([
            ["-hide_banner", "-nostdin", "-y", "-ss", "0.000", "-t", "1.500", "-i", Media(recording)],
            ["-hide_banner", "-nostdin", "-y", "-ss", "1.500", "-t", "2.500", "-i", Media(recording)],
        ]);
        runner.Renders.Take(2).ShouldAllBe(r => !Graph(r).Contains("zoompan") && Graph(r).Contains("[0:a]aresample=48000,apad[a]"));
    }

    [Fact]
    public async Task Only_The_First_Recording_Scene_Fades_In_And_Only_The_Last_Fades_Out()
    {
        var runner = new FakeProcessRunner();

        await Assembler(runner).AssembleAsync(
            Given.Tools(),
            [Clip("s3", 3, "Bake it.", 50000, 80000), Clip("s1", 1, "Hello.", 0, 25000), Clip("s2", 2, "Feed it.", 25000, 50000)],
            _media,
            null,
            Recorded(),
            "gentle",
            CancellationToken.None);

        var graphs = runner.Renders.Take(3).Select(Graph).ToList();
        graphs.Select(g => (g.Contains("fade=t=in:st=0:d=0.4"), g.Contains("fade=t=out"))).ShouldBe([(true, false), (false, false), (false, true)]);
        graphs[2].ShouldContain("fade=t=out:st=29.600:d=0.4");
    }

    [Fact]
    public async Task A_Changed_Recording_Renders_Its_Segments_Again()
    {
        var recording = Recorded();
        OperatorScene[] scenes = [Clip("s1", 1, "Hello.", 0, 25000), Clip("s2", 2, "Bye.", 25000, 50000)];
        var first = await Assembler(new FakeProcessRunner()).AssembleAsync(Given.Tools(), scenes, _media, null, recording, "gentle", CancellationToken.None);
        var same = new FakeProcessRunner();
        await Assembler(same).AssembleAsync(Given.Tools(), scenes, _media, null, recording, "gentle", CancellationToken.None);
        File.WriteAllBytes(Media(recording), [5, 6, 7, 8, 9]);
        File.SetLastWriteTimeUtc(Media(recording), new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var again = new FakeProcessRunner();

        var second = await Assembler(again).AssembleAsync(Given.Tools(), scenes, _media, null, recording, "gentle", CancellationToken.None);

        same.Renders.ShouldHaveSingleItem().ShouldContain("concat");
        second.Segments.ShouldNotContain(s => first.Segments.Contains(s));
        again.Renders.Count.ShouldBe(3);
    }

    [Fact]
    public async Task A_Missing_Recording_Fails_The_Assembly()
    {
        var runner = new FakeProcessRunner();

        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => Assembler(runner).AssembleAsync(
                Given.Tools(), [Clip("s1", 1, "Hello.", 0, 25000)], _media, null, "owner-recording-1a.mp4", "gentle", CancellationToken.None));

        ex.Message.ShouldBe("Scout could not find the recording in the project's media folder.");
        runner.Calls.ShouldBeEmpty();
        Directory.GetFiles(_media).ShouldBeEmpty();
    }
}
