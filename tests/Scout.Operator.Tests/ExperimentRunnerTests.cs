using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Scout.Llm.Abstractions;
using Scout.Operator.Tests.Fakes;
using Scout.Plugin.YouTube;
using Scout.Plugin.YouTube.Abstractions;

namespace Scout.Operator.Tests;

public sealed class ExperimentRunnerTests
{
    private static readonly DateOnly Day1 = new(2026, 11, 1);
    private static readonly DateOnly Day2 = new(2026, 11, 2);

    private readonly IYouTubeAnalyticsClient _analytics = Substitute.For<IYouTubeAnalyticsClient>();
    private readonly IYouTubeReportingClient _reporting = Substitute.For<IYouTubeReportingClient>();

    private static OperatorExperimentArm Arm(string id, long impressions, long clicks, double viewed = 40)
        => new(id, id == "control" ? "Original" : "Question", $"title {id}", null, null, impressions, clicks, viewed);

    private static OperatorProjectState Testing(params OperatorExperimentArm[] arms)
        => Given.Project(status: "approved") with
        {
            Publication = new OperatorPublication("published", "newVideo001", "public", null, false, true, "2026-10-30T12:00:00Z", null, [], [],
                new OperatorExperiment("e1", "title", "running", arms, null, null)),
        };

    private static OperatorExperimentArm Window(OperatorExperimentArm arm, DateOnly day)
        => arm with { StartedAt = $"{day:yyyy-MM-dd}T00:00:00Z", EndedAt = $"{day:yyyy-MM-dd}T23:00:00Z" };

    private void Reach(params YouTubeReachRow[] rows)
    {
        _reporting.FindReachJobAsync(Arg.Any<CancellationToken>()).Returns("job-reach");
        _reporting.ListReportsAsync("job-reach", Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>()).Returns(
            rows.Select(r => r.Date).Distinct().Select(d => new YouTubeReport(
                $"r{d:yyyyMMdd}", new DateTimeOffset(d.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero),
                new DateTimeOffset(d.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero), $"https://youtubereporting.googleapis.com/v1/media/{d:yyyyMMdd}")).ToList());
        foreach (var day in rows.GroupBy(r => r.Date))
        {
            _reporting.DownloadReachAsync($"https://youtubereporting.googleapis.com/v1/media/{day.Key:yyyyMMdd}", Arg.Any<CancellationToken>())
                .Returns(day.ToList());
        }
    }

    private ExperimentRunner Runner(RecordingControlPlane controlPlane, RecordingGate? gate = null)
        => new(controlPlane, _analytics, new ReachReports(_reporting, gate ?? new RecordingGate(), NullLogger<ReachReports>.Instance), new Microsoft.Extensions.Time.Testing.FakeTimeProvider(Given.Now), NullLogger<ExperimentRunner>.Instance);

    [Fact]
    public void A_Click_Through_Rate_Clearly_Higher_With_Enough_Data_Wins()
    {
        var (winner, result) = ExperimentRunner.Decide([Arm("control", 10_000, 400), Arm("b", 10_000, 500)]);

        winner.ShouldBe("b");
        result.ShouldBe("Question has a 5.0% click-through rate against 4.0% for Original (z = 3.41): Question wins.");
    }

    [Fact]
    public void A_Difference_Below_The_Significance_Line_Is_Inconclusive()
    {
        var (winner, result) = ExperimentRunner.Decide([Arm("control", 1_000, 40), Arm("b", 1_000, 50)]);

        winner.ShouldBeNull();
        result.ShouldBe("Inconclusive: Question has a 5.0% click-through rate against 4.0% for Original (z = 1.08), below the 1.96 a winner needs.");
    }

    [Theory]
    [InlineData(999, 100)]
    [InlineData(100_000, 9)]
    public void An_Arm_Under_1000_Impressions_Or_10_Clicks_Makes_The_Experiment_Inconclusive(long impressions, long clicks)
    {
        var (winner, result) = ExperimentRunner.Decide([Arm("control", 10_000, 400), Arm("b", impressions, clicks)]);

        winner.ShouldBeNull();
        result.ShouldBe("Inconclusive: every arm needs at least 1,000 impressions and 10 clicks.");
    }

    [Fact]
    public void A_Winner_Viewers_Watch_More_Than_5_Points_Less_Than_The_Control_Is_Not_A_Winner()
    {
        var (winner, result) = ExperimentRunner.Decide([Arm("control", 10_000, 400, viewed: 40), Arm("b", 10_000, 500, viewed: 34.9)]);

        winner.ShouldBeNull();
        result.ShouldBe("Inconclusive: Question has a 5.0% click-through rate against 4.0% for Original (z = 3.41), but viewers watched 34.9% of it against 40.0% for the control, more than 5 points less.");
    }

    [Fact]
    public void A_Winner_Within_5_Points_Of_The_Control_Wins()
        => ExperimentRunner.Decide([Arm("control", 10_000, 400, viewed: 40), Arm("b", 10_000, 500, viewed: 35)]).Winner.ShouldBe("b");

    [Fact]
    public async Task Ended_Arms_Get_Their_Impressions_And_Clicks_From_The_Reach_Days_They_Covered_And_The_Experiment_Is_Decided()
    {
        var controlPlane = new RecordingControlPlane();
        controlPlane.Store(Testing(Window(Arm("control", 0, 0), Day1), Window(Arm("b", 0, 0), Day2)));
        Reach(
            new YouTubeReachRow(Day1, "newVideo001", 10_000, 0.04),
            new YouTubeReachRow(Day1, "otherVideo1", 50_000, 0.5),
            new YouTubeReachRow(Day2, "newVideo001", 10_000, 0.05));
        _analytics.ReportAsync("newVideo001", Day1, Day1, Arg.Any<CancellationToken>()).Returns(new YouTubeVideoReport(10, 1, 1, 41.0, 0, 0, 0, 0, 0));
        _analytics.ReportAsync("newVideo001", Day2, Day2, Arg.Any<CancellationToken>()).Returns(new YouTubeVideoReport(10, 1, 1, 39.5, 0, 0, 0, 0, 0));

        await Runner(controlPlane).RunAsync(CancellationToken.None);

        var experiment = controlPlane.Saves.ShouldHaveSingleItem().Project.Publication!.Experiment!;
        experiment.Arms.Select(a => (a.Id, a.Impressions, a.Clicks, a.AverageViewPercentage)).ShouldBe([
            ("control", 10_000L, 400L, 41.0),
            ("b", 10_000L, 500L, 39.5),
        ]);
        (experiment.Status, experiment.WinnerArmId).ShouldBe(("concluded", "b"));
        experiment.Result.ShouldBe("Question has a 5.0% click-through rate against 4.0% for Original (z = 3.41): Question wins.");
    }

    [Fact]
    public async Task An_Arm_Whose_Days_No_Report_Covers_Yet_Waits_And_Nothing_Is_Decided()
    {
        var controlPlane = new RecordingControlPlane();
        controlPlane.Store(Testing(Window(Arm("control", 0, 0), Day1), Window(Arm("b", 0, 0), Day2)));
        Reach(new YouTubeReachRow(Day1, "newVideo001", 10_000, 0.04));
        _analytics.ReportAsync(default!, default, default, default).ReturnsForAnyArgs(new YouTubeVideoReport(10, 1, 1, 41.0, 0, 0, 0, 0, 0));

        await Runner(controlPlane).RunAsync(CancellationToken.None);

        var experiment = controlPlane.Saves.ShouldHaveSingleItem().Project.Publication!.Experiment!;
        (experiment.Status, experiment.WinnerArmId, experiment.Result).ShouldBe(("running", null, null));
        experiment.Arms.Select(a => a.Impressions).ShouldBe([10_000L, 0L]);
    }

    [Fact]
    public async Task An_Analytics_Server_Error_Leaves_The_Experiment_As_It_Was()
    {
        var controlPlane = new RecordingControlPlane();
        controlPlane.Store(Testing(Window(Arm("control", 0, 0), Day1), Window(Arm("b", 0, 0), Day2)));
        Reach(new YouTubeReachRow(Day1, "newVideo001", 10_000, 0.04), new YouTubeReachRow(Day2, "newVideo001", 10_000, 0.05));
        _analytics.ReportAsync(default!, default, default, default).ReturnsForAnyArgs<YouTubeVideoReport?>(
            _ => throw new YouTubeApiException("YouTube is currently unavailable. Try again later.", HttpStatusCode.InternalServerError) { Retryable = true });

        await Runner(controlPlane).RunAsync(CancellationToken.None);

        controlPlane.Saves.ShouldBeEmpty();
    }

    [Fact]
    public async Task Without_The_Reporting_Job_Allowed_No_Arm_Is_Measured_And_Nothing_Is_Saved()
    {
        var controlPlane = new RecordingControlPlane();
        controlPlane.Store(Testing(Window(Arm("control", 0, 0), Day1), Window(Arm("b", 0, 0), Day2)));
        _reporting.FindReachJobAsync(Arg.Any<CancellationToken>()).Returns((string?)null);
        var gate = new RecordingGate { Decision = new WriteGateDecision(WriteGateDecisionKind.Refused, "youtubeReporting jobs.create is not declared by youtube", null) };

        await Runner(controlPlane, gate).RunAsync(CancellationToken.None);

        await _reporting.DidNotReceiveWithAnyArgs().CreateReachJobAsync(default);
        controlPlane.Saves.ShouldBeEmpty();
        await _analytics.DidNotReceiveWithAnyArgs().ReportAsync(default!, default, default, default);
    }

    [Fact]
    public async Task The_Day_Of_A_Mid_Day_Switch_Counts_Only_For_The_Arm_Live_Most_Of_That_Day()
    {
        var day3 = new DateOnly(2026, 11, 3);
        var controlPlane = new RecordingControlPlane();
        controlPlane.Store(Testing(
            Arm("control", 0, 0) with { StartedAt = "2026-11-01T00:00:00Z", EndedAt = "2026-11-02T15:00:00Z" },
            Arm("b", 0, 0) with { StartedAt = "2026-11-02T15:00:00Z", EndedAt = "2026-11-03T11:00:00Z" }));
        Reach(
            new YouTubeReachRow(Day1, "newVideo001", 5_000, 0.04),
            new YouTubeReachRow(Day2, "newVideo001", 5_000, 0.04),
            new YouTubeReachRow(day3, "newVideo001", 10_000, 0.05));
        _analytics.ReportAsync(default!, default, default, default).ReturnsForAnyArgs(new YouTubeVideoReport(10, 1, 1, 40, 0, 0, 0, 0, 0));

        await Runner(controlPlane).RunAsync(CancellationToken.None);

        controlPlane.Saves.ShouldHaveSingleItem().Project.Publication!.Experiment!.Arms
            .Select(a => (a.Id, a.Impressions, a.Clicks)).ShouldBe([("control", 10_000L, 400L), ("b", 10_000L, 500L)]);
        await _analytics.Received(1).ReportAsync("newVideo001", Day1, Day2, Arg.Any<CancellationToken>());
        await _analytics.Received(1).ReportAsync("newVideo001", day3, day3, Arg.Any<CancellationToken>());
    }

    [Fact]
    public void Each_Day_Goes_To_The_Arm_Live_For_Most_Of_Its_Hours_Never_To_Both()
    {
        var days = ExperimentRunner.DaysOf(
            [
                Arm("control", 0, 0) with { StartedAt = "2026-11-01T00:00:00Z", EndedAt = "2026-11-02T11:00:00Z" },
                Arm("b", 0, 0) with { StartedAt = "2026-11-02T11:00:00Z", EndedAt = "2026-11-03T00:00:00Z" },
            ],
            Given.Now);

        days["control"].ShouldBe([Day1]);
        days["b"].ShouldBe([Day2]);
    }
}
