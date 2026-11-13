using System.Net;
using System.Text.Json;
using Scout.Plugin.YouTube.Abstractions;
using Scout.Plugin.YouTube.Tests.Fakes;

namespace Scout.Plugin.YouTube.Tests;

public sealed class YouTubeReportingClientTests
{
    private const string Download = "https://youtubereporting.googleapis.com/v1/media/CHANNEL/report-1?alt=media";

    private static (YouTubeReportingClient Client, StubHttpMessageHandler Handler) Build()
    {
        var handler = new StubHttpMessageHandler();
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://youtubereporting.googleapis.com/v1/") };
        return (new YouTubeReportingClient(http), handler);
    }

    [Fact]
    public async Task The_Reach_Job_Is_Found_Among_The_Owner_Jobs_By_Its_Report_Type()
    {
        var (client, handler) = Build();
        handler.EnqueueJson(HttpStatusCode.OK, """
            {"jobs":[{"id":"job-basic","reportTypeId":"channel_basic_a2","name":"Basic"},
                     {"id":"job-reach","reportTypeId":"channel_reach_basic_a1","name":"Scout reach"}]}
            """);

        var id = await client.FindReachJobAsync(CancellationToken.None);

        handler.Requests.ShouldHaveSingleItem().Uri.ToString().ShouldBe("https://youtubereporting.googleapis.com/v1/jobs");
        id.ShouldBe("job-reach");
    }

    [Fact]
    public async Task No_Reach_Job_Is_Found_When_The_Owner_Has_None()
    {
        var (client, handler) = Build();
        handler.EnqueueJson(HttpStatusCode.OK, "{}");

        var id = await client.FindReachJobAsync(CancellationToken.None);

        id.ShouldBeNull();
    }

    [Fact]
    public async Task Creating_The_Reach_Job_Posts_Its_Report_Type()
    {
        var (client, handler) = Build();
        handler.EnqueueJson(HttpStatusCode.OK, """{"id":"job-new","reportTypeId":"channel_reach_basic_a1","name":"Scout reach"}""");

        var id = await client.CreateReachJobAsync(CancellationToken.None);

        id.ShouldBe("job-new");
        var request = handler.Requests.ShouldHaveSingleItem();
        (request.Method, request.Uri.ToString()).ShouldBe(("POST", "https://youtubereporting.googleapis.com/v1/jobs"));
        using var body = JsonDocument.Parse(request.Text);
        body.RootElement.GetProperty("reportTypeId").GetString().ShouldBe("channel_reach_basic_a1");
        body.RootElement.GetProperty("name").GetString().ShouldBe("Scout reach");
    }

    [Fact]
    public async Task Reports_Are_Listed_Created_After_A_Time_Across_Every_Page()
    {
        var (client, handler) = Build();
        handler.EnqueueJson(HttpStatusCode.OK, """
            {"reports":[{"id":"r1","jobId":"job-reach","startTime":"2026-11-03T08:00:00Z","endTime":"2026-11-04T08:00:00Z",
              "createTime":"2026-11-05T10:00:00Z","downloadUrl":"https://youtubereporting.googleapis.com/v1/media/r1"}],
             "nextPageToken":"page 2"}
            """);
        handler.EnqueueJson(HttpStatusCode.OK, """
            {"reports":[{"id":"r2","startTime":"2026-11-04T08:00:00Z","endTime":"2026-11-05T08:00:00Z",
              "downloadUrl":"https://youtubereporting.googleapis.com/v1/media/r2"}]}
            """);

        var reports = await client.ListReportsAsync("job-reach", new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero), CancellationToken.None);

        handler.Requests.Select(r => r.Uri.AbsoluteUri).ShouldBe([
            "https://youtubereporting.googleapis.com/v1/jobs/job-reach/reports?createdAfter=2026-11-01T00%3A00%3A00Z",
            "https://youtubereporting.googleapis.com/v1/jobs/job-reach/reports?createdAfter=2026-11-01T00%3A00%3A00Z&pageToken=page%202",
        ]);
        reports[0].CreateTime.ShouldBe(new DateTimeOffset(2026, 11, 5, 10, 0, 0, TimeSpan.Zero));
        reports[1].CreateTime.ShouldBeNull();
        reports.Select(r => (r.Id, r.StartTime, r.DownloadUrl)).ShouldBe([
            ("r1", new DateTimeOffset(2026, 11, 3, 8, 0, 0, TimeSpan.Zero), "https://youtubereporting.googleapis.com/v1/media/r1"),
            ("r2", new DateTimeOffset(2026, 11, 4, 8, 0, 0, TimeSpan.Zero), "https://youtubereporting.googleapis.com/v1/media/r2"),
        ]);
    }

    [Fact]
    public async Task A_Reach_Report_Is_Read_By_Column_Name_Whatever_The_Column_Order()
    {
        var (client, handler) = Build();
        handler.Enqueue(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                "date,channel_id,video_id,video_thumbnail_impressions_ctr,video_thumbnail_impressions\n" +
                "20261103,UCowner,newVideo001,0.0523,1200\n" +
                "20261103,UCowner,oldVideo001,0.01,300\n"),
        });

        var rows = await client.DownloadReachAsync(Download, CancellationToken.None);

        handler.Requests.ShouldHaveSingleItem().Uri.ToString().ShouldBe(Download);
        rows.ShouldBe([
            new YouTubeReachRow(new DateOnly(2026, 11, 3), "newVideo001", 1200, 0.0523),
            new YouTubeReachRow(new DateOnly(2026, 11, 3), "oldVideo001", 300, 0.01),
        ]);
    }

    [Fact]
    public async Task A_Reach_Report_That_Gives_The_Click_Through_Rate_In_Percent_Is_Read_As_A_Fraction()
    {
        var (client, handler) = Build();
        handler.Enqueue(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                "date,video_id,video_thumbnail_impressions,video_thumbnail_impressions_ctr\n" +
                "20261103,newVideo001,1200,5.23\n" +
                "20261103,oldVideo001,300,0.8\n"),
        });

        var rows = await client.DownloadReachAsync(Download, CancellationToken.None);

        rows.Select(r => r.ImpressionsCtr).ShouldBe([0.0523, 0.008], tolerance: 1e-12);
    }

    [Theory]
    [InlineData("https://evil.example.com/v1/media/report")]
    [InlineData("http://youtubereporting.googleapis.com/v1/media/report")]
    public async Task A_Download_Address_Outside_The_Reporting_Api_Is_Refused_Without_A_Request(string url)
    {
        var (client, handler) = Build();

        var error = await Should.ThrowAsync<YouTubeApiException>(() => client.DownloadReachAsync(url, CancellationToken.None));

        error.Message.ShouldBe(YouTubeReportingClient.UntrustedDownload);
        handler.Requests.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("date,video_id,video_thumbnail_impressions\n20261103,newVideo001,1200\n")]
    [InlineData("date,video_id,video_thumbnail_impressions,video_thumbnail_impressions_ctr\n20261103,newVideo001,lots,0.05\n")]
    [InlineData("date,video_id,video_thumbnail_impressions,video_thumbnail_impressions_ctr\n3 Nov,newVideo001,1200,0.05\n")]
    [InlineData("date,video_id,video_thumbnail_impressions,video_thumbnail_impressions_ctr\n20261103,newVideo001\n")]
    public async Task A_Malformed_Reach_Report_Fails_With_A_Sentence_Instead_Of_Partial_Numbers(string csv)
    {
        var (client, handler) = Build();
        handler.Enqueue(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(csv) });

        var error = await Should.ThrowAsync<YouTubeApiException>(() => client.DownloadReachAsync(Download, CancellationToken.None));

        error.Message.ShouldBe(YouTubeReportingClient.MalformedReport);
    }

    [Fact]
    public async Task A_Reporting_Server_Error_Is_Retryable()
    {
        var (client, handler) = Build();
        handler.EnqueueJson(HttpStatusCode.BadGateway, "{}");

        var error = await Should.ThrowAsync<YouTubeApiException>(() => client.FindReachJobAsync(CancellationToken.None));

        error.Retryable.ShouldBeTrue();
    }
}
