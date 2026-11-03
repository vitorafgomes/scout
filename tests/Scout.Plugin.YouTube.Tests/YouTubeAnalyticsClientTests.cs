using System.Net;
using Scout.Plugin.YouTube.Abstractions;
using Scout.Plugin.YouTube.Tests.Fakes;

namespace Scout.Plugin.YouTube.Tests;

public sealed class YouTubeAnalyticsClientTests
{
    private static readonly DateOnly Start = new(2026, 11, 3);
    private static readonly DateOnly End = new(2026, 11, 4);

    private static (YouTubeAnalyticsClient Client, StubHttpMessageHandler Handler) Build()
    {
        var handler = new StubHttpMessageHandler();
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://youtubeanalytics.googleapis.com/v2/") };
        return (new YouTubeAnalyticsClient(http), handler);
    }

    [Fact]
    public async Task A_Report_Asks_For_The_Video_Totals_Of_The_Owner_Channel_And_Reads_Them_By_Column_Name()
    {
        var (client, handler) = Build();
        handler.EnqueueJson(HttpStatusCode.OK, """
            {"kind":"youtubeAnalytics#resultTable","columnHeaders":[
             {"name":"likes","columnType":"METRIC","dataType":"INTEGER"},
             {"name":"views","columnType":"METRIC","dataType":"INTEGER"},
             {"name":"estimatedMinutesWatched","columnType":"METRIC","dataType":"INTEGER"},
             {"name":"averageViewDuration","columnType":"METRIC","dataType":"INTEGER"},
             {"name":"averageViewPercentage","columnType":"METRIC","dataType":"FLOAT"},
             {"name":"subscribersGained","columnType":"METRIC","dataType":"INTEGER"},
             {"name":"subscribersLost","columnType":"METRIC","dataType":"INTEGER"},
             {"name":"comments","columnType":"METRIC","dataType":"INTEGER"},
             {"name":"shares","columnType":"METRIC","dataType":"INTEGER"}],
             "rows":[[40,1234,2500,121,43.7,12,1,9,5]]}
            """);

        var report = await client.ReportAsync("newVideo001", Start, End, CancellationToken.None);

        handler.Requests.ShouldHaveSingleItem().Uri.ToString().ShouldBe(
            "https://youtubeanalytics.googleapis.com/v2/reports?ids=channel==MINE&startDate=2026-11-03&endDate=2026-11-04" +
            "&metrics=views,estimatedMinutesWatched,averageViewDuration,averageViewPercentage,subscribersGained,subscribersLost,likes,comments,shares" +
            "&filters=video==newVideo001");
        report.ShouldBe(new YouTubeVideoReport(1234, 2500, 121, 43.7, 12, 1, 40, 9, 5));
    }

    [Fact]
    public async Task A_Report_Without_Rows_Means_YouTube_Has_No_Data_Yet()
    {
        var (client, handler) = Build();
        handler.EnqueueJson(HttpStatusCode.OK, """{"columnHeaders":[{"name":"views"}],"rows":[]}""");

        var report = await client.ReportAsync("newVideo001", Start, End, CancellationToken.None);

        report.ShouldBeNull();
    }

    [Fact]
    public async Task Retention_Asks_For_The_Audience_Watch_Ratio_By_Elapsed_Share_Of_One_Video()
    {
        var (client, handler) = Build();
        handler.EnqueueJson(HttpStatusCode.OK, """
            {"columnHeaders":[{"name":"elapsedVideoTimeRatio","columnType":"DIMENSION"},{"name":"audienceWatchRatio","columnType":"METRIC"}],
             "rows":[[0.01,1.0],[0.5,0.42],[1.0,0.1]]}
            """);

        var retention = await client.RetentionAsync("newVideo001", Start, End, CancellationToken.None);

        handler.Requests.ShouldHaveSingleItem().Uri.ToString().ShouldBe(
            "https://youtubeanalytics.googleapis.com/v2/reports?ids=channel==MINE&startDate=2026-11-03&endDate=2026-11-04" +
            "&metrics=audienceWatchRatio&dimensions=elapsedVideoTimeRatio&filters=video==newVideo001");
        retention.ShouldBe([new YouTubeRetentionPoint(0.01, 1.0), new YouTubeRetentionPoint(0.5, 0.42), new YouTubeRetentionPoint(1.0, 0.1)]);
    }

    [Fact]
    public async Task An_Analytics_Server_Error_Is_A_Retryable_Unavailable_Error()
    {
        var (client, handler) = Build();
        handler.EnqueueJson(HttpStatusCode.InternalServerError, "{}");

        var error = await Should.ThrowAsync<YouTubeApiException>(
            () => client.ReportAsync("newVideo001", Start, End, CancellationToken.None));

        (error.Message, error.Retryable).ShouldBe((YouTubeClient.UnavailableMessage, true));
    }

    [Fact]
    public async Task An_Analytics_Answer_That_Is_Not_The_Table_Is_An_Unexpected_Response()
    {
        var (client, handler) = Build();
        handler.EnqueueJson(HttpStatusCode.OK, """{"columnHeaders":[{"name":"views"}],"rows":[["many"]]}""");

        var error = await Should.ThrowAsync<YouTubeApiException>(
            () => client.ReportAsync("newVideo001", Start, End, CancellationToken.None));

        error.Message.ShouldBe(YouTubeAnalyticsClient.Unexpected);
    }
}
