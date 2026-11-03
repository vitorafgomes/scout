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
        ILogger<ReadinessReporter>? logger = null)
        => new(
            controlPlane ?? new RecordingControlPlane(),
            media,
            Options.Create(new YouTubeOptions { TokenFile = tokenFile }),
            time ?? new FakeTimeProvider(Given.Now),
            logger ?? new RecordingLogger<ReadinessReporter>());

    [Fact]
    public void Gemini_Reports_No_Speech_And_No_Images_With_The_Note_That_Narration_Needs_Own_Audio_And_Visuals_Become_Text_Cards()
    {
        var readiness = Reporter(new MediaCapabilities(false, false, "Gemini")).Current();

        (readiness.SpeechAvailable, readiness.ImagesAvailable, readiness.Provider, readiness.FfmpegAvailable)
            .ShouldBe((false, false, "Gemini", false));
        readiness.Notes.ShouldContain(
            "Gemini has no speech or image service in Scout; narration needs your own audio and visuals become text cards.");
    }

    [Fact]
    public void OpenAI_Reports_Speech_And_Images_With_No_Media_Note()
    {
        var token = Path.Combine(_folder, "youtube-token.json");
        File.WriteAllText(token, "{}");

        var readiness = Reporter(new MediaCapabilities(true, true, "OpenAI"), tokenFile: token).Current();

        (readiness.SpeechAvailable, readiness.ImagesAvailable, readiness.Provider, readiness.FfmpegAvailable, readiness.YouTubeSignedIn)
            .ShouldBe((true, true, "OpenAI", false, true));
        readiness.Notes.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(true, false, "AzureOpenAI has no image service in Scout; visuals become text cards unless you attach your own images.")]
    [InlineData(false, true, "AzureOpenAI has no speech service in Scout; narration needs your own audio.")]
    public void A_Provider_With_Only_One_Media_Service_Names_The_One_It_Lacks(bool speech, bool images, string note)
    {
        var token = Path.Combine(_folder, "youtube-token.json");
        File.WriteAllText(token, "{}");

        var readiness = Reporter(new MediaCapabilities(speech, images, "AzureOpenAI"), tokenFile: token).Current();

        readiness.Notes.ShouldBe([note]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("missing-token.json")]
    public void Without_A_YouTube_Token_File_On_Disk_The_Report_Says_Scout_Is_Not_Signed_In(string tokenFile)
    {
        var path = tokenFile.Length == 0 ? string.Empty : Path.Combine(_folder, tokenFile);

        var readiness = Reporter(new MediaCapabilities(true, true, "OpenAI"), tokenFile: path).Current();

        readiness.YouTubeSignedIn.ShouldBeFalse();
        readiness.Notes.ShouldBe(["Scout is not signed in to YouTube; research goes on without the channel's own uploads."]);
    }

    [Fact]
    public void With_No_AI_Source_Connected_The_Note_Says_So()
    {
        var readiness = Reporter(new MediaCapabilities(false, false, "None")).Current();

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

    private static async Task WaitFor(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(10);
        }

        condition().ShouldBeTrue();
    }
}
