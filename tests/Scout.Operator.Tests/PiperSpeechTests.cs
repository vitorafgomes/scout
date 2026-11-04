using Microsoft.Extensions.Time.Testing;
using Scout.Operator.Tests.Fakes;

namespace Scout.Operator.Tests;

public sealed class PiperSpeechTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("scout-piper-").FullName;
    private readonly string _media;
    private readonly string _piper;
    private readonly string _voices;

    public PiperSpeechTests()
    {
        (_piper, _voices) = Given.BundledVoices(_root);
        _media = Directory.CreateDirectory(Path.Combine(_root, "media")).FullName;
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private PiperSpeech Piper(IProcessRunner runner, TimeProvider? time = null) => Given.Piper(runner, _piper, _voices, time);

    [Fact]
    public async Task The_Bundled_Voices_Are_Every_Model_With_Its_Json_One_Per_Speaker_Sorted_By_Id_With_Cori_The_Default()
    {
        var voices = await Piper(new FakeProcessRunner()).ProbeAsync(CancellationToken.None);

        voices.Select(v => (v.Id, v.Label, Path.GetFileName(v.Model), v.Speaker)).ShouldBe([
            ("piper:en_GB-aru-medium#0", "Aru 03 (medium)", "en_GB-aru-medium.onnx", (int?)0),
            ("piper:en_GB-aru-medium#1", "Aru 06 (medium)", "en_GB-aru-medium.onnx", 1),
            ("piper:en_GB-cori-medium", "Cori (medium)", "en_GB-cori-medium.onnx", null),
        ]);
        PiperSpeech.Default(voices).ShouldNotBeNull().Id.ShouldBe("piper:en_GB-cori-medium");
    }

    [Fact]
    public async Task Without_Cori_The_Default_Is_The_First_Voice()
    {
        File.Delete(Path.Combine(_voices, "en_GB-cori-medium.onnx"));

        var voices = await Piper(new FakeProcessRunner()).ProbeAsync(CancellationToken.None);

        PiperSpeech.Default(voices).ShouldNotBeNull().Id.ShouldBe("piper:en_GB-aru-medium#0");
    }

    [Theory]
    [InlineData("missing-piper", "voices")]
    [InlineData("piper", "missing-voices")]
    [InlineData("", "voices")]
    [InlineData("piper", "")]
    public async Task Without_The_Piper_Program_Or_The_Voices_Folder_There_Are_No_Bundled_Voices(string piper, string voices)
    {
        var speech = Given.Piper(
            new FakeProcessRunner(),
            piper.Length == 0 ? string.Empty : Path.Combine(_root, piper),
            voices.Length == 0 ? string.Empty : Path.Combine(_root, voices));

        var probed = await speech.ProbeAsync(CancellationToken.None);

        probed.ShouldBeEmpty();
        PiperSpeech.Default(probed).ShouldBeNull();
        speech.Configured.ShouldBeTrue();
    }

    [Fact]
    public async Task Voices_Found_Are_Kept_And_None_Found_Is_Looked_For_Again_After_Two_Minutes()
    {
        var time = new FakeTimeProvider(Given.Now);
        var found = Piper(new FakeProcessRunner(), time);
        var missing = Path.Combine(_root, "later");
        var notYet = Given.Piper(new FakeProcessRunner(), _piper, missing, time);

        var first = await found.ProbeAsync(CancellationToken.None);
        Directory.Delete(_voices, recursive: true);
        time.Advance(TimeSpan.FromMinutes(10));
        var kept = await found.ProbeAsync(CancellationToken.None);
        var before = await notYet.ProbeAsync(CancellationToken.None);
        Given.BundledVoices(_root);
        Directory.Move(_voices, missing);
        time.Advance(PiperSpeech.RetryAfter - TimeSpan.FromSeconds(1));
        var stillAnswered = await notYet.ProbeAsync(CancellationToken.None);
        time.Advance(TimeSpan.FromSeconds(1));
        var after = await notYet.ProbeAsync(CancellationToken.None);

        kept.ShouldBe(first);
        (before.Count, stillAnswered.Count, after.Count).ShouldBe((0, 0, 3));
    }

    [Fact]
    public async Task Synthesizes_with_argument_list_and_stdin()
    {
        var runner = new FakeProcessRunner();
        var narration = string.Join(' ', Enumerable.Repeat("word", 24)) + "\nFeed the starter daily.";

        await Piper(runner).SynthesizeAsync("piper:en_GB-aru-medium#1", narration, 2, _media, "scene-s2.wav", CancellationToken.None);

        var (program, arguments) = runner.Calls.ShouldHaveSingleItem();
        program.ShouldBe(_piper);
        arguments.Count.ShouldBe(6);
        (arguments[0], arguments[1], arguments[2], arguments[4], arguments[5])
            .ShouldBe(("--model", Path.Combine(_voices, "en_GB-aru-medium.onnx"), "--output_file", "--speaker", "1"));
        Path.GetDirectoryName(arguments[3]).ShouldBe(_media);
        Path.GetFileName(arguments[3]).ShouldStartWith(".");
        Path.GetFileName(arguments[3]).ShouldEndWith(".scene-s2.wav");
        runner.Inputs.ShouldBe([string.Join(' ', Enumerable.Repeat("word", 24)) + " Feed the starter daily."]);
        runner.Timeouts.ShouldBe([TimeSpan.FromMinutes(2) + TimeSpan.FromSeconds(2)]);
        Directory.GetFiles(_media).ShouldBe([Path.Combine(_media, "scene-s2.wav")]);
        new FileInfo(Path.Combine(_media, "scene-s2.wav")).Length.ShouldBe(2048);
        Directory.GetFileSystemEntries(_root).Select(Path.GetFileName).Order().ShouldBe(["media", "piper", "voices"]);
    }

    [Fact]
    public async Task A_Single_Speaker_Voice_Runs_Without_A_Speaker_Argument()
    {
        var runner = new FakeProcessRunner();

        await Piper(runner).SynthesizeAsync("piper:en_GB-cori-medium", "Feed it.", 1, _media, "scene-s1.wav", CancellationToken.None);

        var arguments = runner.Calls.ShouldHaveSingleItem().Arguments;
        (arguments.Count, arguments[0], arguments[1], arguments[2]).ShouldBe(
            (4, "--model", Path.Combine(_voices, "en_GB-cori-medium.onnx"), "--output_file"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Failure_leaves_no_file(bool timesOut)
    {
        var runner = new FakeProcessRunner
        {
            Answer = (_, arguments) =>
            {
                File.WriteAllBytes(arguments[3], new byte[4096]);
                return timesOut
                    ? throw new InvalidOperationException("piper ran longer than 120 seconds and was stopped.")
                    : new ProcessResult(1, string.Empty, "[info] Loaded voice\n[error] Unable to load model\n");
            },
        };

        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => Piper(runner).SynthesizeAsync("piper:en_GB-cori-medium", "Feed it.", 3, _media, "scene-s3.wav", CancellationToken.None));

        ex.Message.ShouldBe(timesOut
            ? "piper ran longer than 120 seconds and was stopped."
            : "Scout could not make the narration of scene 3 with the bundled voice (piper: [error] Unable to load model)");
        Directory.GetFileSystemEntries(_media).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_File_Under_One_Kilobyte_Fails_The_Scene_Saying_The_File_Came_Out_Empty()
    {
        var runner = new FakeProcessRunner
        {
            Answer = (_, arguments) =>
            {
                File.WriteAllBytes(arguments[3], new byte[44]);
                return new ProcessResult(0, string.Empty, string.Empty);
            },
        };

        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => Piper(runner).SynthesizeAsync("piper:en_GB-cori-medium", "Feed it.", 4, _media, "scene-s4.wav", CancellationToken.None));

        ex.Message.ShouldBe("Scout could not make the narration of scene 4 with the bundled voice (the file came out empty)");
        Directory.GetFileSystemEntries(_media).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("piper:en_GB-alan-medium")]
    [InlineData("piper:orphan")]
    [InlineData("piper:en_GB-aru-medium#7")]
    [InlineData("piper:../voices/en_GB-cori-medium")]
    public async Task A_Voice_Id_The_Probe_Did_Not_List_Is_Refused_Before_Anything_Runs(string voice)
    {
        var runner = new FakeProcessRunner();

        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => Piper(runner).SynthesizeAsync(voice, "Feed it.", 1, _media, "scene-s1.wav", CancellationToken.None));

        ex.Message.ShouldBe($"The narration voice '{voice}' is not available on this machine; choose another in Scout Desktop, Production.");
        runner.Calls.ShouldBeEmpty();
        Directory.GetFileSystemEntries(_media).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_Model_Whose_Json_Cannot_Be_Read_Is_Left_Out_And_One_Without_A_Dataset_Is_Named_By_Its_File()
    {
        File.WriteAllBytes(Path.Combine(_voices, "broken.onnx"), [1]);
        File.WriteAllText(Path.Combine(_voices, "broken.onnx.json"), "{ not json");
        File.WriteAllBytes(Path.Combine(_voices, "bare.onnx"), [1]);
        File.WriteAllText(Path.Combine(_voices, "bare.onnx.json"), "{}");

        var voices = await Piper(new FakeProcessRunner()).ProbeAsync(CancellationToken.None);

        voices.Select(v => (v.Id, v.Label)).ShouldBe([
            ("piper:bare", "Bare"),
            ("piper:en_GB-aru-medium#0", "Aru 03 (medium)"),
            ("piper:en_GB-aru-medium#1", "Aru 06 (medium)"),
            ("piper:en_GB-cori-medium", "Cori (medium)"),
        ]);
    }
}
