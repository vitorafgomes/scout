using Scout.Operator.Tests.Fakes;

namespace Scout.Operator.Tests;

public sealed class RhubarbLipSyncTests : IDisposable
{
    private const string Rhubarb = "/opt/scout/rhubarb/rhubarb";

    private readonly string _root = Directory.CreateTempSubdirectory("scout-lipsync-").FullName;
    private readonly string _media;

    public RhubarbLipSyncTests()
    {
        _media = Directory.CreateDirectory(Path.Combine(_root, "p1")).FullName;
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Media(string file) => Path.Combine(_media, file);

    private List<string> Files() => [.. Directory.GetFiles(_media).Select(f => Path.GetFileName(f)!).Order(StringComparer.Ordinal)];

    private Task<LipSync> Cues(FakeProcessRunner runner, string audio = "scene-s1.wav", string sceneId = "s1")
        => Given.LipSync(runner, Rhubarb).CuesAsync(Given.Tools(), _media, sceneId, audio, 3000, CancellationToken.None);

    [Fact]
    public async Task Rhubarb_Runs_With_Exactly_Its_Arguments_On_A_Wav_In_The_Media_Folder()
    {
        File.WriteAllBytes(Media("scene-s1.wav"), [1, 2, 3]);
        var runner = new FakeProcessRunner();

        var lipSync = await Cues(runner);

        var (program, arguments) = runner.Calls.ShouldHaveSingleItem();
        program.ShouldBe(Rhubarb);
        arguments.ShouldBe(["-f", "json", "-r", "phonetic", "--extendedShapes", "GHX", "-o", Media("mouth-cues-s1.json"), Media("scene-s1.wav")]);
        runner.Timeouts.ShouldBe([TimeSpan.FromMinutes(2) + TimeSpan.FromMilliseconds(2 * 3000)]);
        runner.Inputs.ShouldBe([null]);
        lipSync.Failure.ShouldBeNull();
        lipSync.Cues.ShouldBe([new MouthCue(0, 0.12, "X"), new MouthCue(0.12, 0.30, "B"), new MouthCue(0.30, 0.62, "X")]);
        Files().ShouldBe(["mouth-cues-s1.json", "scene-s1.wav"]);
        File.ReadAllBytes(Media("scene-s1.wav")).ShouldBe([1, 2, 3]);
    }

    [Fact]
    public async Task A_Non_Wav_Narration_Is_Converted_First_And_The_Wav_Removed_After()
    {
        File.WriteAllBytes(Media("scene-s1.mp3"), [1, 2, 3]);
        var runner = new FakeProcessRunner();

        var lipSync = await Cues(runner, "scene-s1.mp3");

        runner.Calls.Count.ShouldBe(2);
        var (ffmpeg, conversion) = runner.Calls[0];
        ffmpeg.ShouldBe("/opt/scout/ffmpeg");
        conversion.SkipLast(1).ShouldBe(["-hide_banner", "-nostdin", "-y", "-i", Media("scene-s1.mp3"), "-vn", "-ac", "1", "-ar", "16000", "-c:a", "pcm_s16le"]);
        Path.GetDirectoryName(conversion[^1]).ShouldBe(_media);
        Path.GetFileName(conversion[^1]).ShouldMatch(@"^\.[0-9a-f]{32}\.lipsync-s1\.wav$");
        runner.Calls[1].Arguments[^1].ShouldBe(Media("lipsync-s1.wav"));
        lipSync.Cues.ShouldNotBeNull().Count.ShouldBe(3);
        Files().ShouldBe(["mouth-cues-s1.json", "scene-s1.mp3"]);
    }

    [Theory]
    [InlineData("exit", "Error processing file scene-s1.wav\nUnsupported audio format\n", "Unsupported audio format")]
    [InlineData("timeout", "", "rhubarb ran longer than 120 seconds and was stopped.")]
    [InlineData("missing", "", "Scout could not start rhubarb.")]
    public async Task A_Failing_Or_Slow_Rhubarb_Yields_The_Failure_And_Leaves_No_Cues(string how, string errorTail, string failure)
    {
        File.WriteAllBytes(Media("scene-s1.mp3"), [1, 2, 3]);
        var runner = new FakeProcessRunner
        {
            Answer = (program, arguments) =>
            {
                if (program != Rhubarb)
                {
                    return null;
                }

                File.WriteAllText(arguments[7], "{\"mouthCues\":[{\"start\":0.00,");
                return how switch
                {
                    "exit" => new ProcessResult(1, string.Empty, errorTail),
                    "timeout" => throw new InvalidOperationException(ProcessRunner.TimedOut(program, TimeSpan.FromSeconds(120))),
                    _ => throw new InvalidOperationException(ProcessRunner.NotStarted(program)),
                };
            },
        };

        var lipSync = await Cues(runner, "scene-s1.mp3");

        (lipSync.Cues, lipSync.Failure).ShouldBe(((IReadOnlyList<MouthCue>?)null, failure));
        RhubarbLipSync.Note(lipSync.Failure!).ShouldBe($"Lip sync failed: {failure}");
        Files().ShouldBe(["scene-s1.mp3"]);
    }

    [Theory]
    [InlineData(0, 120_000)]
    [InlineData(600_000, 1_320_000)]
    public async Task Rhubarb_Has_Two_Minutes_Plus_Two_Milliseconds_Per_Millisecond_Of_Narration(int narrationMs, int timeoutMs)
    {
        File.WriteAllBytes(Media("scene-s1.wav"), [1, 2, 3]);
        var runner = new FakeProcessRunner();

        await Given.LipSync(runner, Rhubarb).CuesAsync(Given.Tools(), _media, "s1", "scene-s1.wav", narrationMs, CancellationToken.None);

        runner.Timeouts.ShouldBe([TimeSpan.FromMilliseconds(timeoutMs)]);
    }

    [Fact]
    public async Task A_Narration_Ffmpeg_Cannot_Convert_Yields_The_Failure_Without_Running_Rhubarb_And_Leaves_No_Cues_Or_Wav()
    {
        File.WriteAllBytes(Media("scene-s1.mp3"), [1, 2, 3]);
        var runner = new FakeProcessRunner
        {
            Answer = (program, _) => program == Rhubarb ? null : new ProcessResult(1, string.Empty, "Input #0, mp3\nInvalid data found when processing input\n"),
        };

        var lipSync = await Cues(runner, "scene-s1.mp3");

        (lipSync.Cues, lipSync.Failure).ShouldBe(((IReadOnlyList<MouthCue>?)null, "Scout could not prepare the narration for lip sync (ffmpeg: Invalid data found when processing input)"));
        runner.Calls.ShouldNotContain(c => c.Program == Rhubarb);
        Files().ShouldBe(["scene-s1.mp3"]);
    }

    [Fact]
    public async Task A_Cancelled_Rhubarb_Leaves_No_Cues_File_And_Cancels_The_Caller()
    {
        File.WriteAllBytes(Media("scene-s1.mp3"), [1, 2, 3]);
        var runner = new FakeProcessRunner
        {
            Answer = (program, arguments) =>
            {
                if (program != Rhubarb)
                {
                    return null;
                }

                File.WriteAllText(arguments[7], "{\"mouthCues\":[{\"start\":0.00,");
                throw new OperationCanceledException();
            },
        };

        await Should.ThrowAsync<OperationCanceledException>(() => Cues(runner, "scene-s1.mp3"));

        Files().ShouldBe(["scene-s1.mp3"]);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("{\"mouthCues\":[]}")]
    [InlineData("{\"mouthCues\":[{\"start\":0.0,\"end\":0.2,\"value\":\"Q\"}]}")]
    [InlineData("{\"mouthCues\":[{\"start\":0.3,\"end\":0.2,\"value\":\"A\"}]}")]
    [InlineData("{\"mouthCues\":[{\"start\":\"0\",\"end\":0.2,\"value\":\"A\"}]}")]
    public async Task Unreadable_Cues_Are_A_Failure(string written)
    {
        File.WriteAllBytes(Media("scene-s1.wav"), [1, 2, 3]);
        var runner = new FakeProcessRunner { MouthCues = written };

        var lipSync = await Cues(runner);

        (lipSync.Cues, lipSync.Failure).ShouldBe(((IReadOnlyList<MouthCue>?)null, "Scout could not read the mouth cues rhubarb wrote."));
        Files().ShouldBe(["scene-s1.wav"]);
    }

    [Theory]
    [InlineData("../outside.wav", "s1")]
    [InlineData("scene-s1.wav", "../s1")]
    public async Task A_Narration_Or_Cues_File_Outside_The_Media_Folder_Is_Refused_Before_Anything_Runs(string audio, string sceneId)
    {
        File.WriteAllBytes(Path.Combine(_root, "outside.wav"), [1]);
        File.WriteAllBytes(Media("scene-s1.wav"), [1]);
        var runner = new FakeProcessRunner();

        var ex = await Should.ThrowAsync<InvalidOperationException>(() => Cues(runner, audio, sceneId));

        ex.Message.ShouldBe("Scout refused to write a file outside the project's media folder.");
        runner.Calls.ShouldBeEmpty();
        Directory.GetFiles(_root).Select(Path.GetFileName).ShouldBe(["outside.wav"]);
    }

    [Fact]
    public async Task Cues_Newer_Than_The_Narration_Are_Read_Again_Without_Running_Rhubarb()
    {
        File.WriteAllBytes(Media("scene-s1.wav"), [1, 2, 3]);
        File.SetLastWriteTimeUtc(Media("scene-s1.wav"), new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        await Cues(new FakeProcessRunner());
        var again = new FakeProcessRunner();

        var lipSync = await Cues(again);

        again.Calls.ShouldBeEmpty();
        lipSync.Cues.ShouldNotBeNull().Select(c => c.Shape).ShouldBe(["X", "B", "X"]);
    }
}
