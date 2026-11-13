using Scout.ControlPlane.Operator;
using Shouldly;
using Xunit;

namespace Scout.ControlPlane.Tests.Operator;

/// <summary>The folder of the bundled voices' samples: where it lives, and the worker's grant rule for it.</summary>
public sealed partial class OperatorServiceTests
{
    [Fact]
    public async Task The_samples_directory_is_under_the_media_root()
    {
        var project = await ScriptReadyAsync();
        var projectMedia = await _operator.GetMediaDirectoryAsync(project.Id, CancellationToken.None);
        await RevokeYouTubeAsync();

        var forDesktop = await _operator.GetVoiceSamplesDirectoryAsync(CancellationToken.None);
        var forWorker = await _operator.GetVoiceSamplesDirectoryForWorkerAsync(CancellationToken.None);

        forDesktop.ShouldBe(Path.Combine(_store.MediaRoot, "voice-samples"));
        Path.IsPathFullyQualified(forDesktop).ShouldBeTrue();
        Directory.Exists(forDesktop).ShouldBeTrue();
        Path.GetDirectoryName(forDesktop).ShouldBe(Path.GetDirectoryName(projectMedia));
        forWorker.ShouldBe(forDesktop);
    }

    [Theory]
    [InlineData("a/b.wav")]
    [InlineData("..x.wav")]
    [InlineData("a..b.wav")]
    [InlineData("a\\b.wav")]
    [InlineData("notes.txt")]
    [InlineData("a.mp3")]
    public async Task A_readiness_voice_sample_that_is_not_a_plain_wav_name_is_refused(string sample)
    {
        var report = new Readiness(true, false, "None", false, false, []) { Voices = [new Voice("piper:a", "A", "piper", sample)] };

        var ex = await Should.ThrowAsync<OperatorValidationException>(() => _operator.ReportReadinessAsync(report, CancellationToken.None));

        ex.Message.ShouldBe("A voice sample is a plain .wav file name.");
    }

    [Fact]
    public async Task A_readiness_voice_sample_with_a_plain_name_is_stored()
    {
        var report = new Readiness(true, false, "None", false, false, []) { Voices = [new Voice("piper:a", "A", "piper", "piper-a.wav")] };

        await _operator.ReportReadinessAsync(report, CancellationToken.None);

        (await _operator.GetReadinessAsync(CancellationToken.None)).Voices.Single().SampleFile.ShouldBe("piper-a.wav");
    }
}
