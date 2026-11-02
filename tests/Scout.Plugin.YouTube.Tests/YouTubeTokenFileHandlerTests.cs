using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.SemanticKernel;
using Scout.Plugin.YouTube.Abstractions;
using Scout.Plugin.YouTube.Tests.Fakes;

namespace Scout.Plugin.YouTube.Tests;

public sealed class YouTubeTokenFileHandlerTests : IDisposable
{
    private const string NotConnected = "YouTube is not connected. Sign in from the Scout app.";
    private const string SignInAgain = "The YouTube sign-in is no longer valid. Sign in again in the Scout app.";
    private const string ApiKey = "AIzaSyTestOnlyNotARealKey000000000";

    private readonly string _dir = Directory.CreateTempSubdirectory("scout-youtube-token-").FullName;
    private Func<HttpRequestMessage, HttpResponseMessage> _respond =
        _ => StubHttpMessageHandler.Json(HttpStatusCode.OK, GoogleJson.Channel);

    private readonly StubHttpMessageHandler _stub;

    public YouTubeTokenFileHandlerTests() => _stub = new StubHttpMessageHandler(r => _respond(r));

    private string TokenFile => Path.Combine(_dir, "youtube-token.json");

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private ServiceProvider Compose() => YouTubeComposition.Compose(_stub, tokenFile: TokenFile);

    private void WriteToken(string json, DateTime writtenAtUtc)
    {
        File.WriteAllText(TokenFile, json);
        File.SetLastWriteTimeUtc(TokenFile, writtenAtUtc);
    }

    [Fact]
    public async Task The_Bot_Uses_The_Token_From_The_File_And_The_Rewritten_Token_After_A_Refresh()
    {
        WriteToken("""{"access_token":"ya29.first","expires_at":1793620800000}""", new DateTime(2026, 11, 2, 12, 0, 0, DateTimeKind.Utc));
        using var provider = Compose();
        var client = provider.GetRequiredService<IYouTubeClient>();

        await client.GetMyChannelAsync(CancellationToken.None);
        WriteToken("""{"access_token":"ya29.second","expires_at":1793624400000}""", new DateTime(2026, 11, 2, 12, 55, 0, DateTimeKind.Utc));
        await client.GetMyChannelAsync(CancellationToken.None);

        _stub.Requests.Select(r => r.Authorization).ShouldBe(["Bearer ya29.first", "Bearer ya29.second"]);
    }

    [Fact]
    public void A_Configured_Token_File_Registers_The_Plugin_Before_The_Owner_Signs_In()
    {
        using var provider = Compose();

        provider.GetServices<KernelPlugin>().ShouldHaveSingleItem().Name.ShouldBe("YouTube");
    }

    [Fact]
    public async Task A_Missing_Token_File_Answers_Not_Connected_And_Calls_No_One()
    {
        using var provider = Compose();
        var plugin = provider.GetServices<KernelPlugin>().Single();

        var result = await plugin["youtube_channel_overview"].InvokeAsync(new Kernel());

        result.GetValue<string>().ShouldBe(NotConnected);
        _stub.Requests.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("")]
    [InlineData("{}")]
    [InlineData("not json")]
    [InlineData("""{"access_token":"  ","expires_at":1793620800000}""")]
    [InlineData("""{"access_token":42}""")]
    public async Task An_Empty_Or_Unreadable_Token_File_Fails_As_Not_Connected_Without_A_Call(string contents)
    {
        WriteToken(contents, new DateTime(2026, 11, 2, 12, 0, 0, DateTimeKind.Utc));
        using var provider = Compose();

        var ex = await Should.ThrowAsync<YouTubeApiException>(
            () => provider.GetRequiredService<IYouTubeClient>().GetMyChannelAsync(CancellationToken.None));

        ex.Message.ShouldBe(NotConnected);
        _stub.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_Token_File_Deleted_On_Sign_Out_Stops_The_Old_Token_Being_Sent()
    {
        WriteToken("""{"access_token":"ya29.signed_out"}""", new DateTime(2026, 11, 2, 12, 0, 0, DateTimeKind.Utc));
        using var provider = Compose();
        var client = provider.GetRequiredService<IYouTubeClient>();
        await client.GetMyChannelAsync(CancellationToken.None);

        File.Delete(TokenFile);
        var ex = await Should.ThrowAsync<YouTubeApiException>(() => client.GetMyChannelAsync(CancellationToken.None));

        ex.Message.ShouldBe(NotConnected);
        _stub.Requests.Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_401_From_YouTube_Asks_The_Owner_To_Sign_In_Again_Without_The_Token()
    {
        WriteToken("""{"access_token":"ya29.expired_value"}""", new DateTime(2026, 11, 2, 12, 0, 0, DateTimeKind.Utc));
        _respond = _ => StubHttpMessageHandler.Json(HttpStatusCode.Unauthorized, GoogleJson.Error(401, "authError"));
        using var provider = Compose();

        var ex = await Should.ThrowAsync<YouTubeApiException>(
            () => provider.GetRequiredService<IYouTubeClient>().GetVideosAsync(["abcDEF12345"], CancellationToken.None));

        ex.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        ex.Message.ShouldBe(SignInAgain);
        ex.Message.ShouldNotContain("ya29.expired_value");
    }

    [Fact]
    public async Task A_401_On_My_Channel_After_Sign_In_Asks_To_Sign_In_Again_Not_To_Use_The_Sign_In_Instead_Of_A_Key()
    {
        WriteToken("""{"access_token":"ya29.expired_value"}""", new DateTime(2026, 11, 2, 12, 0, 0, DateTimeKind.Utc));
        _respond = _ => StubHttpMessageHandler.Json(HttpStatusCode.Unauthorized, GoogleJson.Error(401, "authError"));
        using var provider = Compose();
        var plugin = provider.GetServices<KernelPlugin>().Single();

        var result = await plugin["youtube_channel_overview"].InvokeAsync(new Kernel());

        result.GetValue<string>().ShouldBe(SignInAgain);
    }

    [Theory]
    [InlineData("TokenFile")]
    [InlineData("ApiKey")]
    public async Task Every_Request_Any_YouTube_Function_Sends_Is_A_Get(string credential)
    {
        WriteToken("""{"access_token":"ya29.read_only"}""", new DateTime(2026, 11, 2, 12, 0, 0, DateTimeKind.Utc));
        _respond = request => StubHttpMessageHandler.Json(HttpStatusCode.OK, request.RequestUri!.AbsolutePath switch
        {
            "/youtube/v3/channels" => GoogleJson.Channel,
            "/youtube/v3/playlistItems" => GoogleJson.Uploads,
            "/youtube/v3/videos" => GoogleJson.Videos,
            _ => GoogleJson.NoItems,
        });
        using var provider = credential == "ApiKey"
            ? YouTubeComposition.Compose(_stub, apiKey: ApiKey)
            : Compose();
        var plugin = provider.GetServices<KernelPlugin>().Single();
        var arguments = new KernelArguments { ["handle"] = "@ownerchannel", ["count"] = 2, ["videos"] = "abcDEF12345" };

        foreach (var function in plugin)
        {
            await function.InvokeAsync(new Kernel(), arguments);
        }

        plugin.Count().ShouldBe(3);
        _stub.Requests.Select(r => r.Uri.AbsolutePath).Distinct()
            .ShouldBe(["/youtube/v3/channels", "/youtube/v3/playlistItems", "/youtube/v3/videos"], ignoreOrder: true);
        _stub.Requests.ShouldAllBe(r => r.Method == "GET" && r.Uri.Host == "www.googleapis.com");
        if (credential == "ApiKey")
        {
            _stub.Requests.ShouldAllBe(r => r.Uri.Query.EndsWith("&key=" + ApiKey));
            _stub.Requests.ShouldAllBe(r => r.Authorization == null && !r.HeaderValues.Contains(ApiKey));
        }
        else
        {
            _stub.Requests.ShouldAllBe(r => r.Authorization == "Bearer ya29.read_only" && !r.Uri.Query.Contains("key="));
        }
    }
}
