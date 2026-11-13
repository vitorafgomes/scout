using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Scout.Llm.Abstractions;
using Scout.Operator.Tests.Fakes;
using Scout.Plugin.YouTube;

namespace Scout.Operator.Tests;

public sealed class VoiceSamplesTests : IDisposable
{
    private const string Aru0 = "piper-en_GB-aru-medium-0.wav";
    private const string Aru1 = "piper-en_GB-aru-medium-1.wav";
    private const string Cori = "piper-en_GB-cori-medium.wav";

    private readonly string _root = Directory.CreateTempSubdirectory("scout-voice-samples-").FullName;
    private readonly string _data;
    private readonly string _samples;
    private readonly string _piper;
    private readonly string _voices;
    private readonly FakeTimeProvider _time = new(Given.Now);
    private readonly RecordingLogger<VoiceSamples> _logger = new();

    public VoiceSamplesTests()
    {
        (_piper, _voices) = Given.BundledVoices(_root);
        _data = Directory.CreateDirectory(Path.Combine(_root, "operator")).FullName;
        _samples = Directory.CreateDirectory(Path.Combine(_data, "voice-samples")).FullName;
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private VoiceSamples Samples(IProcessRunner runner, RecordingControlPlane? controlPlane = null, string? voices = null)
    {
        controlPlane ??= new RecordingControlPlane { VoiceSamplesDirectory = _samples };
        var piper = Given.Piper(runner, _piper, voices ?? _voices, _time);
        var reporter = new ReadinessReporter(
            controlPlane,
            new MediaCapabilities(false, false, "None"),
            Options.Create(new YouTubeOptions()),
            _time,
            new RecordingLogger<ReadinessReporter>(),
            Given.Toolset(new FakeProcessRunner()),
            piper);
        return new(controlPlane, piper, reporter, _time, _logger);
    }

    private string[] SampleFiles() => [.. Directory.GetFiles(_samples).Select(f => Path.GetFileName(f)).Order(StringComparer.Ordinal)];

    [Fact]
    public async Task Each_bundled_voice_gets_a_sample_once()
    {
        var runner = new FakeProcessRunner();
        var samples = Samples(runner);

        await samples.RunOnceAsync(CancellationToken.None);
        await samples.RunOnceAsync(CancellationToken.None);

        SampleFiles().ShouldBe([Aru0, Aru1, Cori]);
        VoiceSamples.FileFor("piper:en_GB-aru-medium#3").ShouldBe("piper-en_GB-aru-medium-3.wav");
        runner.Calls.Count.ShouldBe(3);
        runner.Inputs.ShouldAllBe(i => i == "Hello. This is how I sound. Scout can narrate your videos with this voice.");
        runner.Calls.Select(c => Path.GetFileName(c.Arguments[1])).ShouldBe(["en_GB-aru-medium.onnx", "en_GB-aru-medium.onnx", "en_GB-cori-medium.onnx"]);
        runner.Calls.Select(c => Path.GetDirectoryName(c.Arguments[3])).ShouldAllBe(d => d == _samples);
        Directory.GetFileSystemEntries(_data).ShouldBe([_samples]);
        _logger.Entries.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_round_that_made_a_sample_reports_readiness_at_once()
    {
        var controlPlane = new RecordingControlPlane { VoiceSamplesDirectory = _samples };
        var samples = Samples(new FakeProcessRunner(), controlPlane);

        await samples.RunOnceAsync(CancellationToken.None);
        var afterFirst = controlPlane.Reports.Count;
        await samples.RunOnceAsync(CancellationToken.None);

        afterFirst.ShouldBe(1);
        controlPlane.Reports[0].Voices.Select(v => v.SampleFile).ShouldBe([Aru0, Aru1, Cori]);
        controlPlane.Reports.Count.ShouldBe(1);
    }

    [Fact]
    public async Task An_existing_sample_is_kept()
    {
        await File.WriteAllTextAsync(Path.Combine(_samples, Cori), "the sample made before");
        var runner = new FakeProcessRunner();

        await Samples(runner).RunOnceAsync(CancellationToken.None);

        runner.Calls.Select(c => Path.GetFileName(c.Arguments[1])).ShouldBe(["en_GB-aru-medium.onnx", "en_GB-aru-medium.onnx"]);
        (await File.ReadAllTextAsync(Path.Combine(_samples, Cori))).ShouldBe("the sample made before");
        SampleFiles().ShouldBe([Aru0, Aru1, Cori]);
    }

    [Fact]
    public async Task A_failing_voice_is_skipped_and_logged_once()
    {
        var runner = new FakeProcessRunner
        {
            Answer = (_, arguments) => arguments.Contains("--speaker") && arguments[^1] == "0"
                ? new ProcessResult(1, string.Empty, "[info] Loaded voice\n[error] Unable to load model\n")
                : null,
        };
        var samples = Samples(runner);

        await samples.RunOnceAsync(CancellationToken.None);
        await samples.RunOnceAsync(CancellationToken.None);

        SampleFiles().ShouldBe([Aru1, Cori]);
        runner.Calls.Count(c => c.Arguments.Contains("--speaker") && c.Arguments[^1] == "0").ShouldBe(2);
        _logger.Entries.ShouldHaveSingleItem().Message.ShouldBe(
            "The sample of the voice piper:en_GB-aru-medium#0 could not be made: Scout could not make the narration of scene 1 with the bundled voice (piper: [error] Unable to load model)");
        Directory.GetFiles(_samples, ".*").ShouldBeEmpty();
    }

    [Theory]
    [InlineData("evil..x", "piper:evil..x")]
    [InlineData("back\\slash", "piper:back\\slash")]
    public async Task A_sample_name_that_would_leave_the_directory_is_refused(string stem, string voice)
    {
        File.WriteAllBytes(Path.Combine(_voices, $"{stem}.onnx"), [1]);
        File.WriteAllText(Path.Combine(_voices, $"{stem}.onnx.json"), "{\"dataset\":\"evil\",\"num_speakers\":1}");
        var runner = new FakeProcessRunner();

        await Samples(runner).RunOnceAsync(CancellationToken.None);

        runner.Calls.ShouldNotContain(c => c.Arguments[1].EndsWith($"{stem}.onnx", StringComparison.Ordinal));
        SampleFiles().ShouldBe([Aru0, Aru1, Cori]);
        Directory.GetFileSystemEntries(_data).ShouldBe([_samples]);
        Directory.GetFileSystemEntries(_root).Select(Path.GetFileName).Order(StringComparer.Ordinal).ShouldBe(["operator", "piper", "voices"]);
        _logger.Entries.ShouldHaveSingleItem().Message.ShouldBe(
            $"The sample of the voice {voice} could not be made: Scout refused to write a file outside the project's media folder.");
    }

    [Fact]
    public async Task A_samples_folder_the_control_plane_refuses_is_logged_once_and_runs_nothing_until_it_answers()
    {
        var controlPlane = new RecordingControlPlane
        {
            VoiceSamplesDirectory = _samples,
            VoiceSamplesFailure = new InvalidOperationException(RecordingControlPlane.NotGranted),
        };
        var runner = new FakeProcessRunner();
        var samples = Samples(runner, controlPlane);

        await samples.RunOnceAsync(CancellationToken.None);
        await samples.RunOnceAsync(CancellationToken.None);
        var callsWhileRefused = runner.Calls.Count;
        controlPlane.VoiceSamplesFailure = null;
        await samples.RunOnceAsync(CancellationToken.None);

        callsWhileRefused.ShouldBe(0);
        SampleFiles().ShouldBe([Aru0, Aru1, Cori]);
        _logger.Entries.ShouldHaveSingleItem().Message.ShouldBe(
            $"The worker could not get the voice samples folder: {RecordingControlPlane.NotGranted}");
    }

    [Fact]
    public async Task Voices_found_after_start_get_their_samples_on_the_next_ten_minute_round_and_a_lost_sample_is_made_again()
    {
        var later = Path.Combine(_root, "later");
        var runner = new FakeProcessRunner();
        using var samples = Samples(runner, voices: later);

        await samples.StartAsync(CancellationToken.None);
        await Task.Delay(50);
        var beforeVoices = runner.Calls.Count;
        Directory.Move(_voices, later);
        _time.Advance(VoiceSamples.Interval);
        await WaitFor(() => SampleFiles().SequenceEqual([Aru0, Aru1, Cori]));
        File.Delete(Path.Combine(_samples, Cori));
        _time.Advance(VoiceSamples.Interval - TimeSpan.FromSeconds(1));
        await Task.Delay(50);
        var beforeTenMinutes = SampleFiles();
        _time.Advance(TimeSpan.FromSeconds(1));
        await WaitFor(() => SampleFiles().SequenceEqual([Aru0, Aru1, Cori]));
        await samples.StopAsync(CancellationToken.None);

        beforeVoices.ShouldBe(0);
        beforeTenMinutes.ShouldBe([Aru0, Aru1]);
        SampleFiles().ShouldBe([Aru0, Aru1, Cori]);
        runner.Calls.Count.ShouldBe(4);
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(10);
        }

        condition().ShouldBeTrue();
    }
}
