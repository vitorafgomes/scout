using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Scout.Operator.Tests.Fakes;
using Scout.Plugin.YouTube;
using Scout.Plugin.YouTube.Abstractions;

namespace Scout.Operator.Tests;

public sealed class PublishedCheckTests
{
    private readonly IYouTubeWriteClient _youtube = Substitute.For<IYouTubeWriteClient>();
    private readonly FakeTimeProvider _time = new(Given.Now);
    private readonly RecordingLogger<PublishedCheck> _logs = new();

    private static OperatorProjectState Scheduled(string publishAt = "2026-11-03T11:00:00Z")
        => Given.Project(status: "approved") with
        {
            Publication = new OperatorPublication("scheduled", "newVideo001", "public", publishAt, false, true, "2026-11-01T12:00:00Z", null, [], [], null),
        };

    private (PublishedCheck Check, RecordingControlPlane ControlPlane) Build(OperatorProjectState project)
    {
        var controlPlane = new RecordingControlPlane();
        controlPlane.Store(project);
        return (new PublishedCheck(controlPlane, _youtube, _time, _logs), controlPlane);
    }

    [Theory]
    [InlineData("public")]
    [InlineData("unlisted")]
    public async Task A_Scheduled_Video_YouTube_Shows_After_Its_Publish_Time_Is_Saved_Published(string privacy)
    {
        _youtube.GetVideoStatusAsync("newVideo001", Arg.Any<CancellationToken>()).Returns(new YouTubeVideoStatus("processed", privacy, null));
        var (check, controlPlane) = Build(Scheduled());

        await check.RunAsync(CancellationToken.None);

        var saved = controlPlane.Saves.ShouldHaveSingleItem().Project.Publication!;
        saved.ShouldBe(Scheduled().Publication! with { Status = "published", Snapshots = saved.Snapshots, Proposals = saved.Proposals });
    }

    [Fact]
    public async Task A_Video_YouTube_Still_Keeps_Private_Stays_Scheduled()
    {
        _youtube.GetVideoStatusAsync("newVideo001", Arg.Any<CancellationToken>()).Returns(new YouTubeVideoStatus("processed", "private", null));
        var (check, controlPlane) = Build(Scheduled());

        await check.RunAsync(CancellationToken.None);

        controlPlane.Saves.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_Video_Before_Its_Publish_Time_Is_Not_Asked_About()
    {
        var (check, _) = Build(Scheduled("2026-11-03T12:00:01Z"));

        await check.RunAsync(CancellationToken.None);

        _youtube.ReceivedCalls().ShouldBeEmpty();
    }

    [Fact]
    public async Task A_Status_Call_YouTube_Fails_With_A_5xx_Leaves_The_Video_Scheduled_And_Is_Logged()
    {
        _youtube.GetVideoStatusAsync("newVideo001", Arg.Any<CancellationToken>()).Returns<YouTubeVideoStatus?>(
            _ => throw new YouTubeApiException("YouTube is currently unavailable. Try again later.", HttpStatusCode.ServiceUnavailable) { Retryable = true });
        var (check, controlPlane) = Build(Scheduled());

        await check.RunAsync(CancellationToken.None);

        controlPlane.Saves.ShouldBeEmpty();
        _logs.Entries.ShouldHaveSingleItem().ShouldBe((LogLevel.Warning,
            "Whether video project p1 went live could not be checked: YouTube is currently unavailable. Try again later."));
    }

    [Fact]
    public async Task YouTube_Is_Asked_About_A_Video_At_Most_Every_15_Minutes()
    {
        _youtube.GetVideoStatusAsync("newVideo001", Arg.Any<CancellationToken>()).Returns(new YouTubeVideoStatus("processed", "private", null));
        var (check, _) = Build(Scheduled());

        await check.RunAsync(CancellationToken.None);
        _time.Advance(TimeSpan.FromMinutes(14));
        await check.RunAsync(CancellationToken.None);
        _time.Advance(TimeSpan.FromMinutes(1));
        await check.RunAsync(CancellationToken.None);

        await _youtube.Received(2).GetVideoStatusAsync("newVideo001", Arg.Any<CancellationToken>());
    }
}
