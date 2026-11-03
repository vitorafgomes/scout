using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Scout.Llm.Abstractions;
using Scout.Operator.Tests.Fakes;
using Scout.Plugin.YouTube;

namespace Scout.Operator.Tests;

public sealed class ReadinessReporterTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("scout-readiness-").FullName;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private ReadinessReporter Reporter(
        MediaCapabilities media,
        RecordingControlPlane? controlPlane = null,
        string tokenFile = "",
        TimeProvider? time = null,
        ILogger<ReadinessReporter>? logger = null, IProcessRunner? runner = null)
        => new(
            controlPlane ?? new RecordingControlPlane(),
            media,
            Options.Create(new YouTubeOptions { TokenFile = tokenFile }),
            time ?? new FakeTimeProvider(Given.Now),
            logger ?? new RecordingLogger<ReadinessReporter>(), Given.Toolset(runner ?? new FakeProcessRunner()));

    [Fact]
    public async Task Gemini_Reports_No_Speech_And_No_Images_With_The_Note_That_Narration_Needs_Own_Audio_And_Visuals_Become_Text_Cards()
    {
        var readiness = await Reporter(new MediaCapabilities(false, false, "Gemini")).CurrentAsync(CancellationToken.None);

        (readiness.SpeechAvailable, readiness.ImagesAvailable, readiness.Provider, readiness.FfmpegAvailable)
            .ShouldBe((false, false, "Gemini", true));
        readiness.Notes.ShouldContain(
            "Gemini has no speech or image service in Scout; narration needs your own audio and visuals become text cards.");
    }

    [Fact]
    public async Task OpenAI_Reports_Speech_And_Images_With_No_Media_Note()
    {
        var token = Path.Combine(_folder, "youtube-token.json");
        File.WriteAllText(token, "{}");

        var readiness = await Reporter(new MediaCapabilities(true, true, "OpenAI"), tokenFile: token).CurrentAsync(CancellationToken.None);

        (readiness.SpeechAvailable, readiness.ImagesAvailable, readiness.Provider, readiness.FfmpegAvailable, readiness.YouTubeSignedIn)
            .ShouldBe((true, true, "OpenAI", true, true));
        readiness.Notes.ShouldBe(["Videos are assembled with ffmpeg and the libopenh264 encoder."]);
    }

    [Theory]
    [InlineData(true, false, "AzureOpenAI has no image service in Scout; visuals become text cards unless you attach your own images.")]
    [InlineData(false, true, "AzureOpenAI has no speech service in Scout; narration needs your own audio.")]
    public async Task A_Provider_With_Only_One_Media_Service_Names_The_One_It_Lacks(bool speech, bool images, string note)
    {
        var token = Path.Combine(_folder, "youtube-token.json");
        File.WriteAllText(token, "{}");

        var readiness = await Reporter(new MediaCapabilities(speech, images, "AzureOpenAI"), tokenFile: token).CurrentAsync(CancellationToken.None);

        readiness.Notes.ShouldBe([note, "Videos are assembled with ffmpeg and the libopenh264 encoder."]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("missing-token.json")]
    public async Task Without_A_YouTube_Token_File_On_Disk_The_Report_Says_Scout_Is_Not_Signed_In(string tokenFile)
    {
        var path = tokenFile.Length == 0 ? string.Empty : Path.Combine(_folder, tokenFile);

        var readiness = await Reporter(new MediaCapabilities(true, true, "OpenAI"), tokenFile: path).CurrentAsync(CancellationToken.None);

        readiness.YouTubeSignedIn.ShouldBeFalse();
        readiness.Notes.ShouldBe(["Scout is not signed in to YouTube; research goes on without the channel's own uploads.", "Videos are assembled with ffmpeg and the libopenh264 encoder."]);
    }

    [Fact]
    public async Task With_No_AI_Source_Connected_The_Note_Says_So()
    {
        var readiness = await Reporter(new MediaCapabilities(false, false, "None")).CurrentAsync(CancellationToken.None);

        readiness.Notes.ShouldContain("No AI Source is connected; narration needs your own audio and visuals become text cards.");
    }

    [Fact]
    public async Task The_Report_Goes_Out_At_Start_And_Every_10_Minutes_And_A_Control_Plane_That_Fails_Is_Logged_Once()
    {
        var controlPlane = new RecordingControlPlane { ReportFailure = new InvalidOperationException("Scout could not reach its control plane.") };
        var time = new FakeTimeProvider(Given.Now);
        var logger = new RecordingLogger<ReadinessReporter>();
        using var reporter = Reporter(new MediaCapabilities(false, false, "Gemini"), controlPlane, time: time, logger: logger);

        await reporter.StartAsync(CancellationToken.None);
        await WaitFor(() => controlPlane.Reports.Count == 1);
        time.Advance(TimeSpan.FromMinutes(10) - TimeSpan.FromSeconds(1));
        await Task.Delay(50);
        var beforeTenMinutes = controlPlane.Reports.Count;
        time.Advance(TimeSpan.FromSeconds(1));
        await WaitFor(() => controlPlane.Reports.Count >= 2);
        await reporter.StopAsync(CancellationToken.None);

        beforeTenMinutes.ShouldBe(1);
        controlPlane.Reports.Select(r => r.Provider).ShouldBe(["Gemini", "Gemini"]);
        logger.Entries.ShouldHaveSingleItem().Message.ShouldBe(
            "The worker could not report its readiness: Scout could not reach its control plane.");
    }

    [Fact]
    public async Task Without_A_Usable_Ffmpeg_The_Report_Says_Videos_Cannot_Be_Assembled_And_Why()
    {
        var token = Path.Combine(_folder, "youtube-token.json");
        File.WriteAllText(token, "{}");
        var runner = new FakeProcessRunner { EncoderList = " ------\n V....D libx264  H.264\n A....D aac  AAC\n" };

        var readiness = await Reporter(new MediaCapabilities(true, true, "OpenAI"), tokenFile: token, runner: runner).CurrentAsync(CancellationToken.None);

        readiness.FfmpegAvailable.ShouldBeFalse();
        readiness.Notes.ShouldBe(["The ffmpeg Scout found has no H.264 or VP9 encoder it can use, so videos cannot be assembled."]);
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
