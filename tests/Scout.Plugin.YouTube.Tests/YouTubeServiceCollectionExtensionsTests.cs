using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.SemanticKernel;
using Scout.Plugin.YouTube.Abstractions;
using Scout.Plugin.YouTube.Tests.Fakes;

namespace Scout.Plugin.YouTube.Tests;

public sealed class YouTubeServiceCollectionExtensionsTests
{
    private static ServiceProvider Compose(string? apiKey, string? tokenFile)
    {
        var services = new ServiceCollection();
        services.AddYouTubePlugin(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Scout:Plugins:YouTube:ApiKey"] = apiKey,
                ["Scout:Plugins:YouTube:TokenFile"] = tokenFile,
            })
            .Build());
        return services.BuildServiceProvider();
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", "")]
    [InlineData("  ", " ")]
    public void No_Api_Key_And_No_Token_File_Registers_No_Plugin_And_No_Client(string? apiKey, string? tokenFile)
    {
        using var provider = Compose(apiKey, tokenFile);

        provider.GetServices<KernelPlugin>().ShouldBeEmpty();
        provider.GetService<IYouTubeClient>().ShouldBeNull();
    }

    [Theory]
    [InlineData("AIzaSyTestOnlyNotARealKey000000000", null)]
    [InlineData(null, "/nonexistent/youtube-token.json")]
    public void An_Api_Key_Or_A_Token_File_Registers_The_YouTube_Plugin_With_Its_Three_Functions(
        string? apiKey, string? tokenFile)
    {
        using var provider = Compose(apiKey, tokenFile);

        provider.GetService<IYouTubeClient>().ShouldNotBeNull();
        var plugin = provider.GetServices<KernelPlugin>().ShouldHaveSingleItem();
        plugin.Name.ShouldBe("YouTube");
        plugin.Select(f => f.Name).ShouldBe(
            ["youtube_channel_overview", "youtube_recent_videos", "youtube_video_stats"], ignoreOrder: true);
    }

    [Fact]
    public void The_Client_Calls_The_Data_Api_With_A_30_Second_Timeout_And_Json_Headers()
    {
        using var provider = Compose("AIzaSyTestOnlyNotARealKey000000000", null);

        var http = provider.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(IYouTubeClient));

        http.BaseAddress.ShouldBe(new Uri("https://www.googleapis.com/youtube/v3/"));
        http.Timeout.ShouldBe(TimeSpan.FromSeconds(30));
        http.DefaultRequestHeaders.UserAgent.ToString().ShouldBe("Scout");
        http.DefaultRequestHeaders.Accept.ToString().ShouldBe("application/json");
        http.DefaultRequestHeaders.Authorization.ShouldBeNull();
    }

    [Fact]
    public async Task A_Token_File_Wins_Over_The_Api_Key()
    {
        var dir = Directory.CreateTempSubdirectory("scout-youtube-both-").FullName;
        try
        {
            var tokenFile = Path.Combine(dir, "youtube-token.json");
            await File.WriteAllTextAsync(tokenFile, """{"access_token":"ya29.wins","expires_at":1793620800000}""");
            var stub = new StubHttpMessageHandler(_ => StubHttpMessageHandler.Json(HttpStatusCode.OK, GoogleJson.Channel));
            using var provider = YouTubeComposition.Compose(
                stub, apiKey: "AIzaSyTestOnlyNotARealKey000000000", tokenFile: tokenFile);

            await provider.GetRequiredService<IYouTubeClient>().GetMyChannelAsync(CancellationToken.None);

            var request = stub.Requests.ShouldHaveSingleItem();
            request.Authorization.ShouldBe("Bearer ya29.wins");
            request.Uri.Query.ShouldNotContain("key=");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void An_Api_Key_Alone_Registers_No_Write_Analytics_Or_Reporting_Client()
    {
        using var provider = Compose("AIzaSyTestOnlyNotARealKey000000000", null);

        provider.GetService<IYouTubeWriteClient>().ShouldBeNull();
        provider.GetService<IYouTubeAnalyticsClient>().ShouldBeNull();
        provider.GetService<IYouTubeReportingClient>().ShouldBeNull();
    }

    [Fact]
    public async Task With_The_Sign_In_The_Write_Analytics_And_Reporting_Clients_Call_Their_Apis_With_The_Owner_Token()
    {
        var dir = Directory.CreateTempSubdirectory("scout-youtube-writes-").FullName;
        try
        {
            var tokenFile = Path.Combine(dir, "youtube-token.json");
            await File.WriteAllTextAsync(tokenFile, """{"access_token":"ya29.owner","expires_at":1793620800000}""");
            var stub = new StubHttpMessageHandler(_ => StubHttpMessageHandler.Json(HttpStatusCode.OK, """{"items":[],"rows":[]}"""));
            using var provider = YouTubeComposition.Compose(stub, tokenFile: tokenFile);

            await provider.GetRequiredService<IYouTubeWriteClient>().GetVideoStatusAsync("newVideo001", CancellationToken.None);
            await provider.GetRequiredService<IYouTubeAnalyticsClient>().ReportAsync(
                "newVideo001", new DateOnly(2026, 11, 3), new DateOnly(2026, 11, 4), CancellationToken.None);
            await provider.GetRequiredService<IYouTubeReportingClient>().FindReachJobAsync(CancellationToken.None);

            stub.Requests.Select(r => (r.Uri.Host, r.Uri.AbsolutePath, r.Authorization)).ShouldBe([
                ("www.googleapis.com", "/youtube/v3/videos", "Bearer ya29.owner"),
                ("youtubeanalytics.googleapis.com", "/v2/reports", "Bearer ya29.owner"),
                ("youtubereporting.googleapis.com", "/v1/jobs", "Bearer ya29.owner"),
            ]);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void The_Write_Client_Waits_As_Long_As_An_Upload_Takes_And_Never_Follows_A_Redirect()
    {
        using var provider = Compose(null, "/nonexistent/youtube-token.json");

        provider.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(IYouTubeWriteClient)).Timeout
            .ShouldBe(Timeout.InfiniteTimeSpan);
        var handler = provider.GetRequiredService<IHttpMessageHandlerFactory>().CreateHandler(nameof(IYouTubeWriteClient));
        while (handler is DelegatingHandler delegating)
        {
            handler = delegating.InnerHandler!;
        }

        var sockets = handler.ShouldBeOfType<SocketsHttpHandler>();
        (sockets.AllowAutoRedirect, sockets.ConnectTimeout).ShouldBe((false, TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public void Null_Services_Throws()
    {
        Should.Throw<ArgumentNullException>(
            () => ((IServiceCollection)null!).AddYouTubePlugin(new ConfigurationBuilder().Build()));
    }

    [Fact]
    public void Null_Configuration_Throws()
    {
        Should.Throw<ArgumentNullException>(() => new ServiceCollection().AddYouTubePlugin(null!));
    }
}
