using Scout.Operator.Tests.Fakes;

namespace Scout.Operator.Tests;

public sealed class MediaProbeTests : IDisposable
{
    private readonly string _media = Directory.CreateTempSubdirectory("scout-probe-").FullName;

    public void Dispose() => Directory.Delete(_media, recursive: true);

    [Theory]
    [InlineData("Input #0, mp3, from 'scene-s1.mp3':\n  Duration: 00:00:12.35, start: 0.025057, bitrate: 128 kb/s\nAt least one output file must be specified", 12350)]
    [InlineData("  Duration: 01:02:03.50, start: 0.000000, bitrate: 1 kb/s", 3_723_500)]
    [InlineData("  Duration: N/A, bitrate: N/A", null)]
    [InlineData("scene-s1.mp3: Invalid data found when processing input", null)]
    public void The_Length_Is_Read_From_The_Duration_Line_Ffmpeg_Writes_About_Its_Input(string report, int? expected)
        => MediaProbe.ParseDurationMs(report).ShouldBe(expected);

    [Fact]
    public async Task Ffmpeg_Reads_The_File_Inside_The_Media_Directory_With_No_Output_And_Its_Error_Exit_Still_Gives_The_Length()
    {
        var runner = new FakeProcessRunner { ProbedMs = 4250 };

        var ms = await new MediaProbe(runner).DurationMsAsync(Given.Tools(), _media, "scene-s1.mp3", CancellationToken.None);

        ms.ShouldBe(4250);
        runner.Calls.ShouldHaveSingleItem().ShouldBe(("/opt/scout/ffmpeg",
            (IReadOnlyList<string>)["-hide_banner", "-nostdin", "-i", Path.Combine(_media, "scene-s1.mp3")]),
            new CallComparer());
    }

    [Fact]
    public async Task A_File_Ffmpeg_Cannot_Read_Fails_With_A_Sentence_Naming_It()
    {
        var runner = new FakeProcessRunner { Answer = (_, _) => new ProcessResult(1, string.Empty, "scene-s1.mp3: Invalid data found when processing input") };

        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => new MediaProbe(runner).DurationMsAsync(Given.Tools(), _media, "scene-s1.mp3", CancellationToken.None));

        ex.Message.ShouldBe("Scout could not read the length of scene-s1.mp3.");
    }

    [Fact]
    public async Task A_File_Name_That_Would_Leave_The_Media_Directory_Is_Refused_Before_Ffmpeg_Runs()
    {
        var runner = new FakeProcessRunner();

        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => new MediaProbe(runner).DurationMsAsync(Given.Tools(), _media, "../secret.mp3", CancellationToken.None));

        ex.Message.ShouldBe("Scout refused to write a file outside the project's media folder.");
        runner.Calls.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(
        "Input #0, mov,mp4,m4a,3gp,3g2,mj2, from 'owner-recording-1a.mp4':\n  Metadata:\n    title           : Video: my kitchen\n  Duration: 00:10:02.50, start: 0.000000, bitrate: 4628 kb/s\n"
            + "  Stream #0:0[0x1](und): Video: h264 (High) (avc1 / 0x31637661), yuv420p(tv, bt709), 1920x1080, 4500 kb/s, 30 fps\n"
            + "  Stream #0:1[0x2](und): Audio: aac (LC) (mp4a / 0x6134706D), 48000 Hz, stereo, fltp, 128 kb/s\nAt least one output file must be specified",
        602_500, true, true)]
    [InlineData(
        "Input #0, mov,mp4,m4a,3gp,3g2,mj2, from 'owner-recording-1a.mp4':\n  Duration: 00:03:00.00, start: 0.000000, bitrate: 130 kb/s\n"
            + "  Stream #0:0[0x1](und): Audio: aac (LC) (mp4a / 0x6134706D), 44100 Hz, stereo, fltp, 128 kb/s\n"
            + "  Stream #0:1[0x0]: Video: mjpeg (Baseline), yuvj420p(pc, bt470bg/unknown/unknown), 600x600, 90k tbr, 90k tbn (attached pic)\nAt least one output file must be specified",
        180_000, false, true)]
    [InlineData(
        "Input #0, matroska,webm, from 'owner-recording-1a.webm':\n  Metadata:\n    comment         : Audio: none\n  Duration: 00:00:45.00, start: 0.000000, bitrate: 900 kb/s\n"
            + "  Stream #0:0(eng): Video: vp9 (Profile 0), yuv420p(tv), 1280x720, SAR 1:1 DAR 16:9, 30 fps, 30 tbr, 1k tbn (default)\nAt least one output file must be specified",
        45_000, true, false)]
    public async Task Inspect_Reads_The_Length_And_Whether_There_Is_A_Picture_And_Sound(string report, int ms, bool picture, bool sound)
    {
        var runner = new FakeProcessRunner { Answer = (_, _) => new ProcessResult(1, string.Empty, report) };

        var streams = await new MediaProbe(runner).InspectAsync(Given.Tools(), _media, "owner-recording-1a.mp4", CancellationToken.None);

        streams.ShouldBe(new MediaStreams(ms, picture, sound));
        runner.Calls.ShouldHaveSingleItem().Arguments.ShouldBe(["-hide_banner", "-nostdin", "-i", Path.Combine(_media, "owner-recording-1a.mp4")]);
    }

    private sealed class CallComparer : IEqualityComparer<(string Program, IReadOnlyList<string> Arguments)>
    {
        public bool Equals((string Program, IReadOnlyList<string> Arguments) x, (string Program, IReadOnlyList<string> Arguments) y)
            => x.Program == y.Program && x.Arguments.SequenceEqual(y.Arguments);

        public int GetHashCode((string Program, IReadOnlyList<string> Arguments) obj) => obj.Program.GetHashCode(StringComparison.Ordinal);
    }
}
