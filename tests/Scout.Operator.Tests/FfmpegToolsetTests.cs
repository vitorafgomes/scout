using Microsoft.Extensions.Time.Testing;
using Scout.Operator.Tests.Fakes;

namespace Scout.Operator.Tests;

public sealed class FfmpegToolsetTests
{
    private const string Separator = " ------\n";

    private static string Listing(params string[] encoders)
        => " V..... = Video\n A..... = Audio\n" + Separator + string.Concat(encoders.Select(e => $" {(e is "aac" or "libopus" ? 'A' : 'V')}....D {e,-20} an encoder\n"));

    public static TheoryData<string, string?> Recorded() => new()
    {
        { FakeProcessRunner.AllEncoders, "libopenh264" },
        { Listing("h264_videotoolbox", "libvpx-vp9", "aac", "libopus"), "h264_videotoolbox" },
        { Listing("libvpx-vp9", "aac", "libopus"), "libvpx-vp9" },
        { Listing("libopenh264", "libvpx-vp9", "libopus"), "libvpx-vp9" },
        { Listing("libopenh264", "libvpx-vp9"), null },
        { Listing("libx264", "mpeg4", "aac"), null },
    };

    [Theory]
    [MemberData(nameof(Recorded))]
    public void The_Encoder_Is_Libopenh264_Then_H264_Videotoolbox_Beside_Aac_Then_Libvpx_Vp9_Beside_Libopus(string encoders, string? expected)
        => FfmpegToolset.PickEncoder(encoders).ShouldBe(expected);

    [Fact]
    public async Task The_Configured_Ffmpeg_Is_Probed_For_Its_Version_Encoders_Filters_And_Demuxers_Once_And_A_Found_Toolset_Is_Kept()
    {
        var runner = new FakeProcessRunner();
        var clock = new FakeTimeProvider(Given.Now);
        var toolset = Given.Toolset(runner, time: clock);

        var first = await toolset.ProbeAsync(CancellationToken.None);
        clock.Advance(TimeSpan.FromHours(1));
        var second = await toolset.ProbeAsync(CancellationToken.None);

        first.ShouldBeSameAs(second);
        first.Tools.ShouldBe(new FfmpegTools("/opt/scout/ffmpeg", Given.Font, "libopenh264"));
        first.Note.ShouldBe("Videos are assembled with ffmpeg and the libopenh264 encoder.");
        runner.Calls.Select(c => (c.Program, string.Join(' ', c.Arguments))).ShouldBe([
            ("/opt/scout/ffmpeg", "-hide_banner -version"),
            ("/opt/scout/ffmpeg", "-hide_banner -encoders"),
            ("/opt/scout/ffmpeg", "-hide_banner -filters"),
            ("/opt/scout/ffmpeg", "-hide_banner -demuxers"),
        ]);
    }

    [Fact]
    public async Task Without_A_Configured_Path_Ffmpeg_Is_Looked_For_On_The_System_Path_Then_In_Homebrew_Then_In_Usr_Local()
    {
        var runner = new FakeProcessRunner
        {
            Answer = (program, _) => program is "ffmpeg" or "/opt/homebrew/bin/ffmpeg" ? throw new InvalidOperationException(ProcessRunner.NotStarted(program)) : null,
        };

        var probe = await Given.Toolset(runner, ffmpeg: " ").ProbeAsync(CancellationToken.None);

        probe.Tools.ShouldNotBeNull().Ffmpeg.ShouldBe("/usr/local/bin/ffmpeg");
        runner.Calls.Where(c => c.Arguments.Contains("-version")).Select(c => c.Program)
            .ShouldBe(["ffmpeg", "/opt/homebrew/bin/ffmpeg", "/usr/local/bin/ffmpeg"]);
    }

    [Fact]
    public async Task A_Configured_Ffmpeg_That_Cannot_Be_Started_Is_Not_Replaced_By_One_Found_Elsewhere()
    {
        var runner = new FakeProcessRunner
        {
            Answer = (program, _) => program == "/opt/scout/ffmpeg" ? throw new InvalidOperationException(ProcessRunner.NotStarted(program)) : null,
        };

        var probe = await Given.Toolset(runner).ProbeAsync(CancellationToken.None);

        (probe.Tools, probe.Note).ShouldBe((null, "Scout could not run ffmpeg, so videos cannot be assembled."));
        runner.Calls.Select(c => c.Program).ShouldBe(["/opt/scout/ffmpeg"]);
    }

    [Fact]
    public async Task A_Probe_That_Found_No_Ffmpeg_Is_Answered_Again_For_Two_Minutes_And_Then_Repeated()
    {
        var installed = false;
        var runner = new FakeProcessRunner
        {
            Answer = (program, _) => installed ? null : throw new InvalidOperationException(ProcessRunner.NotStarted(program)),
        };
        var clock = new FakeTimeProvider(Given.Now);
        var toolset = Given.Toolset(runner, time: clock);

        var missing = await toolset.ProbeAsync(CancellationToken.None);
        installed = true;
        clock.Advance(TimeSpan.FromSeconds(119));
        var stillMissing = await toolset.ProbeAsync(CancellationToken.None);
        clock.Advance(TimeSpan.FromSeconds(1));
        var found = await toolset.ProbeAsync(CancellationToken.None);

        (missing.Tools, stillMissing.Tools).ShouldBe((null, null));
        stillMissing.ShouldBeSameAs(missing);
        found.Tools.ShouldNotBeNull().Ffmpeg.ShouldBe("/opt/scout/ffmpeg");
        runner.Calls.Count(c => c.Arguments.Contains("-version")).ShouldBe(2);
    }

    [Fact]
    public async Task An_Ffmpeg_Built_Without_Gblur_Makes_Assembly_Unavailable_With_A_Note_Naming_The_Missing_Filter()
    {
        var lgplWithoutGblur = FfmpegToolset.Filters.Where(f => f != "gblur").Append("boxblur");

        var probe = await Given.Toolset(new FakeProcessRunner { FilterList = FakeProcessRunner.Filters(lgplWithoutGblur) }).ProbeAsync(CancellationToken.None);

        (probe.Tools, probe.Note).ShouldBe((null, "The ffmpeg Scout found has no gblur filter, so videos cannot be assembled."));
    }

    [Fact]
    public async Task An_Ffmpeg_Built_Without_Drawtext_Makes_Assembly_Unavailable_With_A_Note_Naming_It()
    {
        var probe = await Given.Toolset(new FakeProcessRunner { FilterList = FakeProcessRunner.Filters(FfmpegToolset.Filters.Where(f => f != "drawtext")) })
            .ProbeAsync(CancellationToken.None);

        (probe.Tools, probe.Note).ShouldBe((null, "The ffmpeg Scout found has no drawtext filter, so videos cannot be assembled."));
    }

    [Fact]
    public async Task An_Ffmpeg_Without_The_Lavfi_Device_Makes_Assembly_Unavailable_With_A_Note_Naming_It()
    {
        var withoutLavfi = FakeProcessRunner.AllDemuxers.Replace(" D d lavfi           Libavfilter virtual input device\n", string.Empty, StringComparison.Ordinal);

        var probe = await Given.Toolset(new FakeProcessRunner { DemuxerList = withoutLavfi }).ProbeAsync(CancellationToken.None);

        (probe.Tools, probe.Note).ShouldBe((null, "The ffmpeg Scout found has no lavfi demuxer, so videos cannot be assembled."));
    }

    [Theory]
    [InlineData("-filters")]
    [InlineData("-demuxers")]
    public async Task A_Filter_Or_Demuxer_Listing_That_Times_Out_Makes_Assembly_Unavailable_And_Is_Probed_Again_Later(string listing)
    {
        var runner = new FakeProcessRunner
        {
            Answer = (program, arguments) => arguments.Contains(listing)
                ? throw new InvalidOperationException(ProcessRunner.TimedOut(program, FfmpegToolset.ProbeTimeout))
                : null,
        };
        var clock = new FakeTimeProvider(Given.Now);
        var toolset = Given.Toolset(runner, time: clock);

        var probe = await toolset.ProbeAsync(CancellationToken.None);
        clock.Advance(FfmpegToolset.RetryAfter);
        await toolset.ProbeAsync(CancellationToken.None);

        (probe.Tools, probe.Note).ShouldBe((null, "Scout could not run ffmpeg, so videos cannot be assembled."));
        runner.Calls.Count(c => c.Arguments.Contains(listing)).ShouldBe(2);
    }

    [Fact]
    public void Every_Filter_And_Demuxer_The_Assembly_Uses_Is_Read_From_Ffmpegs_Listings_And_Their_Legends_Are_Not_Names()
    {
        FfmpegToolset.MissingFilter(FakeProcessRunner.AllFilters).ShouldBeNull();
        FfmpegToolset.MissingFilter(" T.. = Timeline support\n | = Source or sink filter\n").ShouldBe("anullsrc");
        FfmpegToolset.MissingDemuxer(FakeProcessRunner.AllDemuxers).ShouldBeNull();
        FfmpegToolset.MissingDemuxer(" D. = Demuxing supported concat lavfi\n ---\n D  aac  raw AAC\n").ShouldBe("concat");
    }

    [Fact]
    public async Task VP9_Encodes_At_Real_Time_Speed_On_Every_Row_With_Opus_Audio_And_H264_With_AAC()
    {
        var vp9 = await Given.Toolset(new FakeProcessRunner { EncoderList = Listing("libvpx-vp9", "aac", "libopus") }).ProbeAsync(CancellationToken.None);

        vp9.Tools!.VideoArguments.ShouldBe([
            "-c:v", "libvpx-vp9", "-b:v", "6M", "-deadline", "realtime", "-cpu-used", "8", "-row-mt", "1", "-pix_fmt", "yuv420p", "-r", "30",
        ]);
        vp9.Tools.AudioArguments.ShouldBe(["-c:a", "libopus", "-b:a", "192k", "-ar", "48000", "-ac", "2"]);
        Given.Tools().VideoArguments.ShouldBe(["-c:v", "libopenh264", "-b:v", "6M", "-pix_fmt", "yuv420p", "-r", "30"]);
        Given.Tools().AudioArguments.ShouldBe(["-c:a", "aac", "-b:a", "192k", "-ar", "48000", "-ac", "2"]);
    }

    [Fact]
    public async Task A_VP9_Command_Gets_Two_Minutes_Plus_Twelve_Times_Its_Media_And_An_H264_One_Four_Times()
    {
        var runner = new FakeProcessRunner { Answer = (_, _) => new ProcessResult(0, string.Empty, string.Empty) };
        var toolset = Given.Toolset(runner);

        await toolset.RunAsync(Given.Tools(FfmpegTools.Vp9), ["-i", "x", "/media/p1/a.mp4"], 10_000, "Scout could not render scene 1", CancellationToken.None);
        await toolset.RunAsync(Given.Tools(), ["-i", "x", "/media/p1/b.mp4"], 10_000, "Scout could not render scene 1", CancellationToken.None);

        runner.Timeouts.ShouldBe([TimeSpan.FromSeconds(240), TimeSpan.FromSeconds(160)]);
    }

    [Fact]
    public async Task An_Ffmpeg_That_Cannot_Be_Started_Makes_Assembly_Unavailable_With_A_Note_Saying_So()
    {
        var runner = new FakeProcessRunner
        {
            Answer = (program, _) => throw new InvalidOperationException(ProcessRunner.NotStarted(program)),
        };

        var probe = await Given.Toolset(runner).ProbeAsync(CancellationToken.None);

        probe.Tools.ShouldBeNull();
        probe.Note.ShouldBe("Scout could not run ffmpeg, so videos cannot be assembled.");
    }

    [Theory]
    [InlineData("")]
    [InlineData("/nonexistent/fonts/NotoSans-Regular.ttf")]
    public async Task Without_A_Font_File_Assembly_Is_Unavailable_With_A_Note_Saying_So(string font)
    {
        var probe = await Given.Toolset(new FakeProcessRunner(), font).ProbeAsync(CancellationToken.None);

        (probe.Tools, probe.Note).ShouldBe((null, "Scout has no font for captions and text cards, so videos cannot be assembled."));
    }

    [Fact]
    public async Task An_Ffmpeg_Without_A_Usable_Encoder_Makes_Assembly_Unavailable_With_A_Note_Saying_So()
    {
        var probe = await Given.Toolset(new FakeProcessRunner { EncoderList = Listing("libx264", "aac") }).ProbeAsync(CancellationToken.None);

        (probe.Tools, probe.Note).ShouldBe((null, "The ffmpeg Scout found has no H.264 or VP9 encoder it can use, so videos cannot be assembled."));
    }

    [Fact]
    public async Task A_Command_That_Exits_With_An_Error_Fails_With_The_Reason_And_Ffmpegs_Last_Line()
    {
        var runner = new FakeProcessRunner { Answer = (_, _) => new ProcessResult(1, string.Empty, "frame=0\nError opening input file scene-s1.png.\n") };

        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => Given.Toolset(runner).RunAsync(Given.Tools(), ["-i", "x"], 1000, "Scout could not render scene 1", CancellationToken.None));

        ex.Message.ShouldBe("Scout could not render scene 1 (ffmpeg: Error opening input file scene-s1.png.)");
    }

    [Fact]
    public async Task A_Command_That_Runs_Past_Its_Timeout_Fails_With_The_Runners_Sentence()
    {
        var runner = new FakeProcessRunner { Answer = (program, _) => throw new InvalidOperationException(ProcessRunner.TimedOut(program, TimeSpan.FromMinutes(2))) };

        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => Given.Toolset(runner).RunAsync(Given.Tools(), ["-i", "x"], 0, "Scout could not render scene 1", CancellationToken.None));

        ex.Message.ShouldBe("ffmpeg ran longer than 120 seconds and was stopped.");
    }
}
