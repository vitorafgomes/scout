using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using Scout.Llm.Abstractions;
using Scout.Operator.Tests.Fakes;
using Scout.Plugin.YouTube;
using Scout.Plugin.YouTube.Abstractions;

namespace Scout.Operator.Tests;

public sealed class ReachReportsTests
{
    private static readonly DateOnly Day1 = new(2026, 11, 1);
    private static readonly DateOnly Day2 = new(2026, 11, 2);

    private readonly IYouTubeReportingClient _reporting = Substitute.For<IYouTubeReportingClient>();

    private static YouTubeReport Report(DateOnly day)
        => new($"r{day.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}", new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero),
            new DateTimeOffset(day.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero), $"https://youtubereporting.googleapis.com/v1/media/{day.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}");

    [Fact]
    public async Task The_Reporting_Job_Is_Created_Once_Through_The_Write_Gate_And_Recorded()
    {
        _reporting.FindReachJobAsync(Arg.Any<CancellationToken>()).Returns((string?)null);
        _reporting.CreateReachJobAsync(Arg.Any<CancellationToken>()).Returns("job-new");
        _reporting.ListReportsAsync("job-new", Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>()).Returns([]);
        var gate = new RecordingGate();

        var data = await new ReachReports(_reporting, gate, NullLogger<ReachReports>.Instance).LoadAsync(Day1, CancellationToken.None);

        gate.Requests.ShouldBe([("youtube", "youtubeReporting jobs.create", "channel_reach_basic_a1", ReachReports.JobSummary)]);
        gate.Records.ShouldBe([("youtube", "youtubeReporting jobs.create", "channel_reach_basic_a1", "succeeded", null)]);
        data.ShouldNotBeNull().Rows.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_Refused_Reporting_Job_Is_Never_Created_And_Means_No_Reach_Data()
    {
        _reporting.FindReachJobAsync(Arg.Any<CancellationToken>()).Returns((string?)null);
        var gate = new RecordingGate { Decision = new WriteGateDecision(WriteGateDecisionKind.Refused, "not declared", null) };

        var data = await new ReachReports(_reporting, gate, NullLogger<ReachReports>.Instance).LoadAsync(Day1, CancellationToken.None);

        data.ShouldBeNull();
        await _reporting.DidNotReceiveWithAnyArgs().CreateReachJobAsync(default);
        gate.Records.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_Malformed_Report_Is_Left_Out_And_Its_Day_Counts_As_Not_Covered()
    {
        _reporting.FindReachJobAsync(Arg.Any<CancellationToken>()).Returns("job-reach");
        _reporting.ListReportsAsync("job-reach", Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>()).Returns([Report(Day1), Report(Day2)]);
        _reporting.DownloadReachAsync(Report(Day1).DownloadUrl, Arg.Any<CancellationToken>())
            .Returns([new YouTubeReachRow(Day1, "newVideo001", 1000, 0.05)]);
        _reporting.DownloadReachAsync(Report(Day2).DownloadUrl, Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<YouTubeReachRow>>(_ => throw new YouTubeApiException("YouTube returned a reach report Scout could not read."));

        var data = (await new ReachReports(_reporting, new RecordingGate(), NullLogger<ReachReports>.Instance).LoadAsync(Day1, CancellationToken.None))!;

        data.Totals("newVideo001", Day1, Day1).ShouldBe((1000L, 50L));
        data.Totals("newVideo001", Day1, Day2).ShouldBeNull();
    }

    [Fact]
    public async Task Reporting_That_Cannot_Be_Reached_Means_No_Reach_Data()
    {
        _reporting.FindReachJobAsync(Arg.Any<CancellationToken>())
            .Returns<string?>(_ => throw new HttpRequestException("Connection reset by peer"));

        var data = await new ReachReports(_reporting, new RecordingGate(), NullLogger<ReachReports>.Instance).LoadAsync(Day1, CancellationToken.None);

        data.ShouldBeNull();
    }

    [Fact]
    public async Task A_Day_Reported_Twice_Counts_Only_Its_Latest_Report_And_A_Report_Is_Downloaded_Once()
    {
        var older = Report(Day1) with { Id = "r-old", DownloadUrl = "https://youtubereporting.googleapis.com/v1/media/old", CreateTime = new DateTimeOffset(2026, 11, 2, 0, 0, 0, TimeSpan.Zero) };
        var newer = Report(Day1) with { Id = "r-new", DownloadUrl = "https://youtubereporting.googleapis.com/v1/media/new", CreateTime = new DateTimeOffset(2026, 11, 3, 0, 0, 0, TimeSpan.Zero) };
        _reporting.FindReachJobAsync(Arg.Any<CancellationToken>()).Returns("job-reach");
        _reporting.ListReportsAsync("job-reach", Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>()).Returns([newer, older]);
        _reporting.DownloadReachAsync(older.DownloadUrl, Arg.Any<CancellationToken>()).Returns([new YouTubeReachRow(Day1, "newVideo001", 1000, 0.05)]);
        _reporting.DownloadReachAsync(newer.DownloadUrl, Arg.Any<CancellationToken>()).Returns([new YouTubeReachRow(Day1, "newVideo001", 1200, 0.05)]);
        var reach = new ReachReports(_reporting, new RecordingGate(), NullLogger<ReachReports>.Instance);

        var first = (await reach.LoadAsync(Day1, CancellationToken.None))!;
        var second = (await reach.LoadAsync(Day1, CancellationToken.None))!;

        first.Totals("newVideo001", Day1, Day1).ShouldBe((1200L, 60L));
        second.Totals("newVideo001", Day1, Day1).ShouldBe((1200L, 60L));
        await _reporting.DidNotReceive().DownloadReachAsync(older.DownloadUrl, Arg.Any<CancellationToken>());
        await _reporting.Received(1).DownloadReachAsync(newer.DownloadUrl, Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_Reporting_Job_YouTube_Fails_To_Create_Is_Recorded_Failed_And_Means_No_Reach_Data(bool reset)
    {
        _reporting.FindReachJobAsync(Arg.Any<CancellationToken>()).Returns((string?)null);
        _reporting.CreateReachJobAsync(Arg.Any<CancellationToken>()).Returns<string>(_ => reset
            ? throw new HttpRequestException("Connection reset by peer")
            : throw new YouTubeApiException("YouTube is currently unavailable. Try again later.", System.Net.HttpStatusCode.BadGateway) { Retryable = true });
        var gate = new RecordingGate();

        var data = await new ReachReports(_reporting, gate, NullLogger<ReachReports>.Instance).LoadAsync(Day1, CancellationToken.None);

        data.ShouldBeNull();
        gate.Records.ShouldBe([("youtube", "youtubeReporting jobs.create", "channel_reach_basic_a1", reset ? "failed: HttpRequestException" : "failed: YouTubeApiException", null)]);
    }

    [Fact]
    public async Task The_Reporting_Job_Is_Recorded_Against_The_Id_The_Gate_Answered()
    {
        _reporting.FindReachJobAsync(Arg.Any<CancellationToken>()).Returns((string?)null);
        _reporting.CreateReachJobAsync(Arg.Any<CancellationToken>()).Returns("job-new");
        _reporting.ListReportsAsync("job-new", Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>()).Returns([]);
        var gate = new RecordingGate { Decision = new WriteGateDecision(WriteGateDecisionKind.Allowed, string.Empty, "started-1") };

        await new ReachReports(_reporting, gate, NullLogger<ReachReports>.Instance).LoadAsync(Day1, CancellationToken.None);

        gate.Records.ShouldBe([("youtube", "youtubeReporting jobs.create", "channel_reach_basic_a1", "succeeded", "started-1")]);
    }
}
