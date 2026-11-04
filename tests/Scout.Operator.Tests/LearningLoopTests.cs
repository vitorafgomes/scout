using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Microsoft.SemanticKernel.ChatCompletion;
using Scout.Llm.Abstractions;
using Scout.Operator.Tests.Fakes;
using Scout.Plugin.YouTube;
using Scout.Plugin.YouTube.Abstractions;

namespace Scout.Operator.Tests;

public sealed class LearningLoopTests : IDisposable
{
    private static readonly YouTubeVideoReport Report = new(1234, 2500, 121, 43.7, 12, 1, 40, 9, 5);

    private readonly string _media = Directory.CreateTempSubdirectory("scout-learning-").FullName;
    private readonly IYouTubeAnalyticsClient _analytics = Substitute.For<IYouTubeAnalyticsClient>();
    private readonly IYouTubeReportingClient _reporting = Substitute.For<IYouTubeReportingClient>();
    private readonly IYouTubeWriteClient _youtubeWrites = Substitute.For<IYouTubeWriteClient>();
    private readonly FakeTimeProvider _time = new(Given.Now);

    public LearningLoopTests()
    {
        _analytics.ReportAsync(default!, default, default, default).ReturnsForAnyArgs(Report);
        _analytics.RetentionAsync(default!, default, default, default).ReturnsForAnyArgs([new YouTubeRetentionPoint(0.5, 0.42)]);
        _reporting.FindReachJobAsync(Arg.Any<CancellationToken>()).Returns("job-reach");
        _reporting.ListReportsAsync(default!, default, default).ReturnsForAnyArgs([]);
    }

    public void Dispose() => Directory.Delete(_media, recursive: true);

    private static OperatorProjectState Video(
        string id = "p1", string topic = "sourdough", string status = "published", string uploadedAt = "2026-10-30T12:00:00Z",
        string? publishAt = null, params OperatorSnapshot[] snapshots)
        => Given.Project(id, topic, "approved") with
        {
            Publication = new OperatorPublication(status, $"vid-{id}", "public", publishAt, false, true, uploadedAt, null, snapshots,
                [new OperatorProposal("old-1", "title", "Owner kept this", "earlier", "accepted", null)], null),
        };

    private static OperatorSnapshot Snap(string window, long views)
        => new(window, "2026-10-01T09:00:00Z", views, 100, 60, 30.0, 1, 0, 2, 1, 0, null, null, []);

    private (LearningLoop Loop, RecordingControlPlane ControlPlane) Build(
        ScriptedChat chat, IReadOnlyList<OperatorProjectState> projects, OperatorMandate? mandate = null, RecordingGate? gate = null, Microsoft.SemanticKernel.Kernel? media = null,
        Func<OperatorProjectState, bool>? failOn = null, FakeTimeProvider? clock = null)
    {
        var time = clock ?? _time;
        var controlPlane = new RecordingControlPlane { MediaDirectory = _media, Mandate = mandate ?? Given.Mandate(), FailOnProject = failOn ?? (_ => false) };
        foreach (var project in projects)
        {
            controlPlane.Store(project);
        }

        var kernel = media ?? Given.Media();
        var loop = new LearningLoop(
            controlPlane,
            _analytics,
            new ReachReports(_reporting, gate ?? new RecordingGate(), NullLogger<ReachReports>.Instance),
            chat,
            new VisualGenerator(kernel, Given.Capabilities(kernel), time),
            new PublishedCheck(controlPlane, _youtubeWrites, time, NullLogger<PublishedCheck>.Instance),
            time,
            NullLogger<LearningLoop>.Instance);
        return (loop, controlPlane);
    }

    private static string Proposals(params (string Kind, string Text)[] items)
        => "{\"proposals\":[" + string.Join(",", items.Select(i => $"{{\"kind\":\"{i.Kind}\",\"text\":\"{i.Text}\",\"reason\":\"CTR below the channel\"}}")) + "]}";

    [Fact]
    public async Task A_Published_Video_Gets_Its_24h_Snapshot_Once_The_Day_And_The_72_Hour_Latency_Have_Passed()
    {
        var (loop, controlPlane) = Build(ScriptedChat.Answering(Proposals()), [Video()]);
        _reporting.ListReportsAsync(default!, default, default).ReturnsForAnyArgs([
            new YouTubeReport("r1", new DateTimeOffset(2026, 10, 30, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 10, 31, 0, 0, 0, TimeSpan.Zero), "https://youtubereporting.googleapis.com/v1/media/r1"),
            new YouTubeReport("r2", new DateTimeOffset(2026, 10, 31, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero), "https://youtubereporting.googleapis.com/v1/media/r2"),
        ]);
        _reporting.DownloadReachAsync("https://youtubereporting.googleapis.com/v1/media/r1", Arg.Any<CancellationToken>())
            .Returns([new YouTubeReachRow(new DateOnly(2026, 10, 30), "vid-p1", 15_000, 0.04)]);
        _reporting.DownloadReachAsync("https://youtubereporting.googleapis.com/v1/media/r2", Arg.Any<CancellationToken>())
            .Returns([new YouTubeReachRow(new DateOnly(2026, 10, 31), "vid-p1", 5_000, 0.08)]);

        await loop.SnapshotAsync(CancellationToken.None);

        await _analytics.Received(1).ReportAsync("vid-p1", new DateOnly(2026, 10, 30), new DateOnly(2026, 10, 31), Arg.Any<CancellationToken>());
        var snapshot = controlPlane.Saves.ShouldHaveSingleItem().Project.Publication!.Snapshots.ShouldHaveSingleItem();
        (snapshot with { Retention = [] }).ShouldBe(new OperatorSnapshot("24h", "2026-11-03T12:00:00Z", 1234, 2500, 121, 43.7, 12, 1, 40, 9, 5, 20_000, 0.05, []));
        snapshot.Retention.ShouldBe([new OperatorRetentionPoint(0.5, 0.42)]);
    }

    [Fact]
    public async Task A_Window_Still_Inside_The_Latency_Takes_No_Snapshot()
    {
        var (loop, controlPlane) = Build(ScriptedChat.Answering(Proposals()), [Video(uploadedAt: "2026-10-31T12:00:01Z")]);

        await loop.SnapshotAsync(CancellationToken.None);

        _analytics.ReceivedCalls().ShouldBeEmpty();
        controlPlane.Saves.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_Scheduled_Video_Counts_Its_Windows_From_The_Publish_Time()
    {
        var (loop, _) = Build(ScriptedChat.Answering(Proposals()), [Video(status: "scheduled", uploadedAt: "2026-10-20T12:00:00Z", publishAt: "2026-10-30T10:00:00Z")]);

        await loop.SnapshotAsync(CancellationToken.None);

        await _analytics.Received(1).ReportAsync("vid-p1", new DateOnly(2026, 10, 30), new DateOnly(2026, 10, 31), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_Private_Upload_Or_A_Window_Already_Taken_Gets_No_Snapshot()
    {
        var (loop, controlPlane) = Build(ScriptedChat.Answering(Proposals()), [
            Video("p1", status: "uploaded"),
            Video("p2", snapshots: Snap("24h", 10)),
        ]);

        await loop.SnapshotAsync(CancellationToken.None);

        _analytics.ReceivedCalls().ShouldBeEmpty();
        controlPlane.Saves.ShouldBeEmpty();
    }

    [Fact]
    public async Task Without_The_Reporting_Job_The_Snapshot_Is_Saved_Without_Impressions()
    {
        _reporting.FindReachJobAsync(Arg.Any<CancellationToken>()).Returns((string?)null);
        var gate = new RecordingGate { Decision = new WriteGateDecision(WriteGateDecisionKind.Refused, "youtubeReporting jobs.create is not declared by youtube", null) };
        var (loop, controlPlane) = Build(ScriptedChat.Answering(Proposals()), [Video()], gate: gate);

        await loop.SnapshotAsync(CancellationToken.None);

        var snapshot = controlPlane.Saves.ShouldHaveSingleItem().Project.Publication!.Snapshots.ShouldHaveSingleItem();
        (snapshot.Views, snapshot.Impressions, snapshot.ImpressionsCtr).ShouldBe((1234L, (long?)null, (double?)null));
    }

    [Fact]
    public async Task An_Analytics_Server_Error_Takes_No_Snapshot_And_Asks_For_No_Proposal()
    {
        _analytics.ReportAsync(default!, default, default, default).ReturnsForAnyArgs<YouTubeVideoReport?>(
            _ => throw new YouTubeApiException("YouTube is currently unavailable. Try again later.", HttpStatusCode.ServiceUnavailable) { Retryable = true });
        var chat = ScriptedChat.Answering(Proposals());
        var (loop, controlPlane) = Build(chat, [Video()]);

        await loop.SnapshotAsync(CancellationToken.None);

        controlPlane.Saves.ShouldBeEmpty();
        chat.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task After_A_Snapshot_Up_To_Three_Proposals_Are_Saved_Proposed_With_The_Thumbnail_Picture_Beside_Them()
    {
        var chat = ScriptedChat.Answering(Proposals(
            ("title", "Sourdough in ten minutes, really"),
            ("thumbnail", "A dark loaf on a white table"),
            ("topic", "Rye starters"),
            ("title", "A fourth one")));
        var (loop, controlPlane) = Build(chat, [Video()], media: Given.Media(images: Given.Images([9, 9, 9])));

        await loop.SnapshotAsync(CancellationToken.None);

        var proposals = controlPlane.Saves[^1].Project.Publication!.Proposals;
        proposals.ShouldBe([
            new OperatorProposal("old-1", "title", "Owner kept this", "earlier", "accepted", null),
            new OperatorProposal("24h-1", "title", "Sourdough in ten minutes, really", "CTR below the channel", "proposed", null),
            new OperatorProposal("24h-2", "thumbnail", "A dark loaf on a white table", "CTR below the channel", "proposed", "thumbnail-proposal-24h-2.png"),
            new OperatorProposal("24h-3", "topic", "Rye starters", "CTR below the channel", "proposed", null),
        ]);
        File.ReadAllBytes(Path.Combine(_media, "thumbnail-proposal-24h-2.png")).ShouldBe([9, 9, 9]);
        chat.Offers.ShouldAllBe(o => o.Kernel == null && o.Settings == null);
    }

    [Fact]
    public async Task The_Proposals_Compare_The_Video_With_The_Channels_Earlier_Snapshots_Of_The_Same_Window()
    {
        var chat = ScriptedChat.Answering(Proposals(("title", "Better")));
        var (loop, _) = Build(chat, [Video(), Video("p0", "rye bread", snapshots: [Snap("24h", 777), Snap("7d", 999)])]);

        await loop.SnapshotAsync(CancellationToken.None);

        var request = chat.Requests.ShouldHaveSingleItem().Single(m => m.Role == AuthorRole.User).Text;
        request.ShouldContain("Its first 24h: 1,234 views");
        request.ShouldContain("- rye bread: 777 views");
        request.ShouldNotContain("999 views");
    }

    [Fact]
    public async Task A_Model_That_Fails_Leaves_The_Snapshot_Saved_And_The_Evening_Run_Proposes_For_It()
    {
        var chat = new ScriptedChat(() => throw new HttpRequestException("model down"), () => throw new HttpRequestException("model down"), () => Proposals(("title", "Later")));
        var (loop, controlPlane) = Build(chat, [Video()]);

        await loop.SnapshotAsync(CancellationToken.None);
        controlPlane.Saves.ShouldHaveSingleItem().Project.Publication!.Proposals.Count.ShouldBe(1);
        await loop.ProposeAsync(CancellationToken.None);
        await loop.ProposeAsync(CancellationToken.None);

        controlPlane.Saves[^1].Project.Publication!.Proposals.Select(p => p.Id).ShouldBe(["old-1", "24h-1"]);
        controlPlane.Saves.Count.ShouldBe(2);
    }

    [Theory]
    [InlineData("weekly", "2026-10-26T12:00:00Z", true)]
    [InlineData("weekly", "2026-10-28T12:00:00Z", false)]
    [InlineData("one video a day", "2026-11-02T11:00:00Z", false)]
    [InlineData("daily", "2026-11-02T11:00:00Z", true)]
    [InlineData("when inspired", "2025-01-01T00:00:00Z", false)]
    public async Task The_Next_Topic_Is_Proposed_Only_When_The_Mandate_Cadence_Says_One_Is_Due(string cadence, string lastCreated, bool proposes)
    {
        var chat = ScriptedChat.Answering(Proposals(("topic", "Rye starters")));
        var (loop, controlPlane) = Build(chat, [Video() with { CreatedAt = lastCreated }], Given.Mandate() with { Cadence = cadence });

        await loop.ProposeNextTopicAsync(CancellationToken.None);

        if (proposes)
        {
            controlPlane.Saves.ShouldHaveSingleItem().Project.Publication!.Proposals[^1]
                .ShouldBe(new OperatorProposal("topic-2026-11-03", "topic", "Rye starters", "CTR below the channel", "proposed", null));
        }
        else
        {
            controlPlane.Saves.ShouldBeEmpty();
            chat.Requests.ShouldBeEmpty();
        }
    }

    [Fact]
    public async Task A_Proposal_Id_Carries_The_London_Date_Not_The_Utc_Date()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 7, 1, 23, 30, 0, TimeSpan.Zero));
        var (loop, controlPlane) = Build(ScriptedChat.Answering(Proposals(("topic", "Rye starters"))), [Video() with { CreatedAt = "2026-06-01T12:00:00Z" }], clock: clock);

        await loop.ProposeNextTopicAsync(CancellationToken.None);

        controlPlane.Saves.ShouldHaveSingleItem().Project.Publication!.Proposals[^1].Id.ShouldEndWith("2026-07-02");
    }

    [Fact]
    public async Task The_Weekly_Review_Is_One_Strategy_Proposal_Saved_Once_A_Day_Whatever_The_Restarts()
    {
        var chat = ScriptedChat.Answering(Proposals(("strategy", "Shorter hooks worked; keep them.")));
        var (loop, controlPlane) = Build(chat, [Video(snapshots: Snap("7d", 5000))]);

        await loop.ReviewWeekAsync(CancellationToken.None);
        await loop.ReviewWeekAsync(CancellationToken.None);

        controlPlane.Saves.ShouldHaveSingleItem().Project.Publication!.Proposals[^1]
            .ShouldBe(new OperatorProposal("strategy-2026-11-03", "strategy", "Shorter hooks worked; keep them.", "CTR below the channel", "proposed", null));
        chat.Requests.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task The_Morning_Run_First_Saves_A_Scheduled_Video_That_Went_Live_As_Published()
    {
        _youtubeWrites.GetVideoStatusAsync("vid-p1", Arg.Any<CancellationToken>()).Returns(new YouTubeVideoStatus("processed", "public", null));
        var (loop, controlPlane) = Build(ScriptedChat.Answering(Proposals()), [Video(status: "scheduled", publishAt: "2026-11-02T10:00:00Z")]);

        await loop.SnapshotAsync(CancellationToken.None);

        controlPlane.Saves.ShouldHaveSingleItem().Project.Publication!.Status.ShouldBe("published");
    }

    [Fact]
    public async Task A_Thumbnail_Proposal_Without_A_Picture_Is_Not_Saved()
    {
        var chat = ScriptedChat.Answering(Proposals(("title", "Sourdough, really"), ("thumbnail", "A dark loaf")));
        var (loop, controlPlane) = Build(chat, [Video()]);

        await loop.SnapshotAsync(CancellationToken.None);

        controlPlane.Saves[^1].Project.Publication!.Proposals.Select(p => (p.Id, p.Kind)).ShouldBe([("old-1", "title"), ("24h-1", "title")]);
    }

    [Fact]
    public async Task Proposals_That_Cannot_Be_Saved_For_One_Video_Are_Logged_And_The_Next_Video_Still_Gets_Its_Own()
    {
        var chat = ScriptedChat.Answering(Proposals(("title", "Better")));
        var (loop, controlPlane) = Build(
            chat,
            [Video("p1", snapshots: Snap("24h", 10)), Video("p2", "rye bread", snapshots: Snap("24h", 20))],
            failOn: p => p.Id == "p2");

        await loop.ProposeAsync(CancellationToken.None);

        controlPlane.Saves.Select(s => s.Project.Id).ShouldBe(["p2", "p1"]);
        controlPlane.Saves[^1].Project.Publication!.Proposals.Select(p => p.Id).ShouldBe(["old-1", "24h-1"]);
    }
}
