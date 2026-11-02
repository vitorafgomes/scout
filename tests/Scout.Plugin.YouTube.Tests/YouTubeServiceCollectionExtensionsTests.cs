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
