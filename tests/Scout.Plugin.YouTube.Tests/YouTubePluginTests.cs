using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.SemanticKernel;
using Scout.Plugin.YouTube.Tests.Fakes;

namespace Scout.Plugin.YouTube.Tests;

public sealed class YouTubePluginTests
{
    private const string ApiKey = "AIzaSyTestOnlyNotARealKey000000000";

    private static (YouTubePlugin plugin, StubHttpMessageHandler handler) Build()
    {
        var handler = new StubHttpMessageHandler();
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://www.googleapis.com/youtube/v3/") };
        return (new YouTubePlugin(new YouTubeClient(http)), handler);
    }

    private static void ArrangeFailure(StubHttpMessageHandler handler, string failure)
    {
        switch (failure)
        {
            case "timeout":
                handler.EnqueueThrow(new TaskCanceledException("timed out"));
                break;
            case "transport":
                handler.EnqueueThrow(new HttpRequestException("connection reset"));
                break;
            case "quota":
                handler.EnqueueJson(HttpStatusCode.Forbidden, GoogleJson.Error(403, "quotaExceeded"));
                break;
            case "html":
                handler.EnqueueJson(HttpStatusCode.OK, "<html><body>Sign in to the network</body></html>");
                break;
            default:
                var status = Enum.Parse<HttpStatusCode>(failure);
                handler.EnqueueJson(status, GoogleJson.Error((int)status, "backendError"));
                break;
        }
    }

    [Fact]
    public async Task Channel_Overview_Of_My_Channel_Lists_Title_Subscribers_Views_And_Video_Count()
    {
        var (plugin, handler) = Build();
        handler.EnqueueJson(HttpStatusCode.OK, GoogleJson.Channel);

        var result = await plugin.ChannelOverviewAsync();

        handler.Requests.ShouldHaveSingleItem().Uri.Query.ShouldContain("mine=true");
        result.ShouldBe("""
            Title: Owner Channel
            Handle: @ownerchannel
            Subscribers: 45,600
            Total views: 1,234,567
            Videos: 321
            Published: 2019-03-04
            """.ReplaceLineEndings("\n"));
    }

    [Fact]
    public async Task Recent_Videos_Lists_The_Latest_Uploads_With_Their_Stats()
    {
        var (plugin, handler) = Build();
        handler.EnqueueJson(HttpStatusCode.OK, GoogleJson.Channel);
        handler.EnqueueJson(HttpStatusCode.OK, GoogleJson.Uploads);
        handler.EnqueueJson(HttpStatusCode.OK, GoogleJson.Videos);

        var result = await plugin.RecentVideosAsync(count: 2);

        handler.Requests[1].Uri.Query.ShouldContain("playlistId=UUowner0000000000000000&maxResults=2");
        result.ShouldBe("""
            2026-10-30 | Building a bot | 1:02:03 | 15,230 views | 1,200 likes | 87 comments | https://www.youtube.com/watch?v=abcDEF12345
            2026-10-20 | Short tip | 0:45 | 980 views | hidden likes | hidden comments | https://www.youtube.com/watch?v=xyz_-987654
            """.ReplaceLineEndings("\n"));
    }

    [Fact]
    public async Task Video_Stats_Accepts_Ids_And_Urls_And_Names_Missing_Ones()
    {
        var (plugin, handler) = Build();
        handler.EnqueueJson(HttpStatusCode.OK, GoogleJson.Videos);

        var result = await plugin.VideoStatsAsync(
            "https://www.youtube.com/watch?v=abcDEF12345&t=42s, youtu.be/xyz_-987654 https://youtube.com/shorts/gone0000000 notavideo");

        handler.Requests.ShouldHaveSingleItem().Uri.Query
            .ShouldEndWith("&id=abcDEF12345,xyz_-987654,gone0000000");
        result.ShouldBe("""
            Building a bot
            Channel: Owner Channel
            Published: 2026-10-30
            Duration: 1:02:03
            Views: 15,230
            Likes: 1,200
            Comments: 87
            URL: https://www.youtube.com/watch?v=abcDEF12345

            Short tip
            Channel: Owner Channel
            Published: 2026-10-20
            Duration: 0:45
            Views: 980
            Likes: hidden
            Comments: hidden
            URL: https://www.youtube.com/watch?v=xyz_-987654

            Not found: gone0000000, notavideo
            """.ReplaceLineEndings("\n"));
    }

    [Fact]
    public async Task Video_Stats_Looks_Up_At_Most_50_Videos_And_Names_The_Rest_As_Not_Looked_Up()
    {
        var (plugin, handler) = Build();
        handler.EnqueueJson(HttpStatusCode.OK, GoogleJson.NoItems);
        var ids = Enumerable.Range(0, 51).Select(i => $"v{i:0000000000}").ToList();

        var result = await plugin.VideoStatsAsync(string.Join(",", ids));

        handler.Requests.ShouldHaveSingleItem().Uri.Query.ShouldEndWith("&id=" + string.Join(",", ids.Take(50)));
        result.Split('\n').ShouldBe([
            "Not found: " + string.Join(", ", ids.Take(50)),
            "",
            "Not looked up (at most 50 per request): v0000000050",
        ]);
    }

    [Fact]
    public async Task Video_Stats_Without_An_Id_Or_Link_Calls_No_One()
    {
        var (plugin, handler) = Build();

        var result = await plugin.VideoStatsAsync("my latest one");

        result.ShouldBe("No YouTube video id or link found in the request.");
        handler.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task Channel_Overview_By_Handle_Works_With_An_Api_Key()
    {
        var stub = new StubHttpMessageHandler(_ => StubHttpMessageHandler.Json(HttpStatusCode.OK, GoogleJson.Channel));
        using var provider = YouTubeComposition.Compose(stub, apiKey: ApiKey);
        var plugin = provider.GetServices<KernelPlugin>().Single();

        var result = await plugin["youtube_channel_overview"].InvokeAsync(
            new Kernel(), new KernelArguments { ["handle"] = "@ownerchannel" });

        stub.Requests.ShouldHaveSingleItem().Uri.Query
            .ShouldBe($"?part=snippet,statistics,contentDetails&forHandle=ownerchannel&key={ApiKey}");
        result.GetValue<string>()!.Split('\n')
            .ShouldBe(["Title: Owner Channel", "Handle: @ownerchannel", "Subscribers: 45,600",
                "Total views: 1,234,567", "Videos: 321", "Published: 2019-03-04"]);
    }

    [Fact]
    public async Task Channel_Overview_By_An_Unknown_Handle_Says_No_Channel_Has_It()
    {
        var (plugin, handler) = Build();
        handler.EnqueueJson(HttpStatusCode.OK, GoogleJson.NoItems);

        var result = await plugin.ChannelOverviewAsync("@nobody");

        result.ShouldBe("No YouTube channel has the handle @nobody.");
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized,
        "Sign in from the Scout app to read your own channel; an API key reads public channels by handle only.")]
    [InlineData(HttpStatusCode.BadRequest, "YouTube rejected the request (required).")]
    public async Task My_Channel_With_Only_An_Api_Key_Explains_The_Sign_In(HttpStatusCode status, string expected)
    {
        var stub = new StubHttpMessageHandler(_ => StubHttpMessageHandler.Json(status, GoogleJson.Error((int)status, "required")));
        using var provider = YouTubeComposition.Compose(stub, apiKey: ApiKey);
        var plugin = provider.GetServices<KernelPlugin>().Single();

        var result = await plugin["youtube_channel_overview"].InvokeAsync(new Kernel());

        result.GetValue<string>().ShouldBe(expected);
        stub.Requests.ShouldHaveSingleItem().Uri.Query.ShouldContain("mine=true");
    }

    [Theory]
    [InlineData("Unauthorized", "The YouTube sign-in is no longer valid. Sign in again in the Scout app.")]
    [InlineData("quota", "YouTube's daily quota for this key is spent; it resets at midnight Pacific time.")]
    [InlineData("Forbidden", "The YouTube credentials cannot read this.")]
    [InlineData("NotFound", "YouTube could not find that channel, playlist or video.")]
    [InlineData("InternalServerError", "YouTube is currently unavailable. Try again later.")]
    [InlineData("timeout", "YouTube request timed out. Try again later.")]
    [InlineData("transport", "Could not reach YouTube. Try again later.")]
    [InlineData("html", "YouTube returned an unexpected response.")]
    public async Task Channel_Overview_Returns_Clean_Error_Without_Throwing(string failure, string expected)
    {
        var (plugin, handler) = Build();
        ArrangeFailure(handler, failure);

        var result = await plugin.ChannelOverviewAsync("@ownerchannel");

        result.ShouldBe(expected);
    }

    [Theory]
    [InlineData("quota", "YouTube's daily quota for this key is spent; it resets at midnight Pacific time.")]
    [InlineData("NotFound", "YouTube could not find that channel, playlist or video.")]
    [InlineData("InternalServerError", "YouTube is currently unavailable. Try again later.")]
    [InlineData("timeout", "YouTube request timed out. Try again later.")]
    [InlineData("transport", "Could not reach YouTube. Try again later.")]
    public async Task Recent_Videos_Returns_Clean_Error_Without_Throwing(string failure, string expected)
    {
        var (plugin, handler) = Build();
        handler.EnqueueJson(HttpStatusCode.OK, GoogleJson.Channel);
        ArrangeFailure(handler, failure);

        var result = await plugin.RecentVideosAsync("@ownerchannel");

        result.ShouldBe(expected);
        handler.Requests.Count.ShouldBe(2);
    }

    [Theory]
    [InlineData("quota", "YouTube's daily quota for this key is spent; it resets at midnight Pacific time.")]
    [InlineData("Forbidden", "The YouTube credentials cannot read this.")]
    [InlineData("InternalServerError", "YouTube is currently unavailable. Try again later.")]
    [InlineData("timeout", "YouTube request timed out. Try again later.")]
    [InlineData("transport", "Could not reach YouTube. Try again later.")]
    public async Task Video_Stats_Returns_Clean_Error_Without_Throwing(string failure, string expected)
    {
        var (plugin, handler) = Build();
        ArrangeFailure(handler, failure);

        var result = await plugin.VideoStatsAsync("abcDEF12345");

        result.ShouldBe(expected);
    }

    [Fact]
    public void Ctor_Throws_When_Client_Null()
    {
        Should.Throw<ArgumentNullException>(() => new YouTubePlugin(null!));
    }
}
