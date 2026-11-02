using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Scout.Plugin.YouTube.Abstractions;
using Scout.Plugin.YouTube.Tests.Fakes;

namespace Scout.Plugin.YouTube.Tests;

public sealed class YouTubeClientTests
{
    private static (YouTubeClient client, StubHttpMessageHandler handler) Build()
    {
        var handler = new StubHttpMessageHandler();
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://www.googleapis.com/youtube/v3/") };
        return (new YouTubeClient(http), handler);
    }

    [Fact]
    public async Task Channel_By_Handle_Asks_For_The_Handle_Without_The_At_Sign_And_Reads_The_String_Counts()
    {
        var (client, handler) = Build();
        handler.EnqueueJson(HttpStatusCode.OK, GoogleJson.Channel);

        var channel = await client.GetChannelByHandleAsync("@ownerchannel", CancellationToken.None);

        handler.Requests.ShouldHaveSingleItem().Uri.PathAndQuery
            .ShouldBe("/youtube/v3/channels?part=snippet,statistics,contentDetails&forHandle=ownerchannel");
        channel.ShouldBe(new YouTubeChannel(
            "UCowner0000000000000000", "Owner Channel", "@ownerchannel", 45600, 1234567, 321,
            new DateTimeOffset(2019, 3, 4, 10, 0, 0, TimeSpan.Zero), "UUowner0000000000000000"));
    }

    [Fact]
    public async Task Channel_By_Handle_Is_Null_When_No_Channel_Has_The_Handle()
    {
        var (client, handler) = Build();
        handler.EnqueueJson(HttpStatusCode.OK, GoogleJson.NoItems);

        var channel = await client.GetChannelByHandleAsync("nobody", CancellationToken.None);

        channel.ShouldBeNull();
    }

    [Fact]
    public async Task A_Hidden_Subscriber_Count_Is_Reported_As_Unknown()
    {
        var (client, handler) = Build();
        handler.EnqueueJson(HttpStatusCode.OK, GoogleJson.ChannelWithHiddenSubscribers);

        var channel = await client.GetChannelByHandleAsync("quiet", CancellationToken.None);

        channel.ShouldNotBeNull().Subscribers.ShouldBeNull();
    }

    [Fact]
    public async Task My_Channel_Asks_For_Mine()
    {
        var (client, handler) = Build();
        handler.EnqueueJson(HttpStatusCode.OK, GoogleJson.Channel);

        await client.GetMyChannelAsync(CancellationToken.None);

        handler.Requests.ShouldHaveSingleItem().Uri.PathAndQuery
            .ShouldBe("/youtube/v3/channels?part=snippet,statistics,contentDetails&mine=true");
    }

    [Fact]
    public async Task Uploads_Read_The_Playlist_Then_All_Its_Videos_In_One_Call()
    {
        var (client, handler) = Build();
        handler.EnqueueJson(HttpStatusCode.OK, GoogleJson.Uploads);
        handler.EnqueueJson(HttpStatusCode.OK, GoogleJson.Videos);

        var videos = await client.ListUploadsAsync("UUowner0000000000000000", 5, CancellationToken.None);

        handler.Requests.Select(r => r.Uri.PathAndQuery).ShouldBe([
            "/youtube/v3/playlistItems?part=contentDetails&playlistId=UUowner0000000000000000&maxResults=5",
            "/youtube/v3/videos?part=snippet,statistics,contentDetails&id=abcDEF12345,xyz_-987654",
        ]);
        videos.ShouldBe([
            new YouTubeVideo("abcDEF12345", "Building a bot", "Owner Channel",
                new DateTimeOffset(2026, 10, 30, 18, 0, 0, TimeSpan.Zero), new TimeSpan(1, 2, 3), 15230, 1200, 87),
            new YouTubeVideo("xyz_-987654", "Short tip", "Owner Channel",
                new DateTimeOffset(2026, 10, 20, 18, 0, 0, TimeSpan.Zero), TimeSpan.FromSeconds(45), 980, null, null),
        ]);
    }

    [Fact]
    public async Task Uploads_Ask_For_At_Most_50_Items()
    {
        var (client, handler) = Build();
        handler.EnqueueJson(HttpStatusCode.OK, GoogleJson.NoItems);

        await client.ListUploadsAsync("UUowner", 500, CancellationToken.None);

        handler.Requests.ShouldHaveSingleItem().Uri.Query.ShouldEndWith("&maxResults=50");
    }

    [Fact]
    public async Task Videos_Are_Asked_For_At_Most_50_Ids_In_One_Call()
    {
        var (client, handler) = Build();
        handler.EnqueueJson(HttpStatusCode.OK, GoogleJson.NoItems);
        var ids = Enumerable.Range(0, 60).Select(i => $"video{i:000000}").ToList();

        await client.GetVideosAsync(ids, CancellationToken.None);

        var query = handler.Requests.ShouldHaveSingleItem().Uri.Query;
        query[(query.IndexOf("&id=", StringComparison.Ordinal) + 4)..].Split(',').ShouldBe(ids.Take(50));
    }

    [Fact]
    public async Task Unauthorized_Throws_Sign_In_Again_Message()
    {
        var (client, handler) = Build();
        handler.EnqueueJson(HttpStatusCode.Unauthorized, GoogleJson.Error(401, "authError"));

        var ex = await Should.ThrowAsync<YouTubeApiException>(
            () => client.GetChannelByHandleAsync("ownerchannel", CancellationToken.None));

        ex.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        ex.Message.ShouldBe("The YouTube sign-in is no longer valid. Sign in again in the Scout app.");
    }

    [Theory]
    [InlineData("quotaExceeded")]
    [InlineData("dailyLimitExceeded")]
    public async Task Quota_Exceeded_Throws_Quota_Message(string reason)
    {
        var (client, handler) = Build();
        handler.EnqueueJson(HttpStatusCode.Forbidden, GoogleJson.Error(403, reason));

        var ex = await Should.ThrowAsync<YouTubeApiException>(
            () => client.GetVideosAsync(["abcDEF12345"], CancellationToken.None));

        ex.Message.ShouldBe("YouTube's daily quota for this key is spent; it resets at midnight Pacific time.");
    }

    [Theory]
    [InlineData("""{"error":{"code":403,"errors":[{"reason":"forbidden"}]}}""")]
    [InlineData("<html>Forbidden</html>")]
    [InlineData("")]
    public async Task Forbidden_Without_Quota_Throws_Permissions_Message(string body)
    {
        var (client, handler) = Build();
        handler.EnqueueJson(HttpStatusCode.Forbidden, body);

        var ex = await Should.ThrowAsync<YouTubeApiException>(
            () => client.GetVideosAsync(["abcDEF12345"], CancellationToken.None));

        ex.Message.ShouldBe("The YouTube credentials cannot read this.");
    }

    [Fact]
    public async Task NotFound_Throws_NotFound_Message()
    {
        var (client, handler) = Build();
        handler.EnqueueJson(HttpStatusCode.NotFound, GoogleJson.Error(404, "playlistNotFound"));

        var ex = await Should.ThrowAsync<YouTubeApiException>(
            () => client.ListUploadsAsync("UUghost", 10, CancellationToken.None));

        ex.Message.ShouldBe("YouTube could not find that channel, playlist or video.");
    }

    [Theory]
    [InlineData("""{"error":{"code":400,"errors":[{"reason":"keyInvalid"}]}}""", "YouTube rejected the request (keyInvalid).")]
    [InlineData("""{"error":{"code":400,"errors":[{"reason":"key=AIza secret"}]}}""", "YouTube rejected the request.")]
    [InlineData("not json", "YouTube rejected the request.")]
    public async Task Bad_Request_Names_Only_A_Plain_Reason(string body, string expected)
    {
        var (client, handler) = Build();
        handler.EnqueueJson(HttpStatusCode.BadRequest, body);

        var ex = await Should.ThrowAsync<YouTubeApiException>(
            () => client.GetChannelByHandleAsync("ownerchannel", CancellationToken.None));

        ex.Message.ShouldBe(expected);
    }

    [Fact]
    public async Task ServerError_Throws_Unavailable_Message()
    {
        var (client, handler) = Build();
        handler.EnqueueJson(HttpStatusCode.ServiceUnavailable, GoogleJson.Error(503, "backendError"));

        var ex = await Should.ThrowAsync<YouTubeApiException>(
            () => client.GetMyChannelAsync(CancellationToken.None));

        ex.Message.ShouldBe("YouTube is currently unavailable. Try again later.");
    }

    [Fact]
    public async Task Timeout_Propagates_As_OperationCanceled()
    {
        var (client, handler) = Build();
        handler.EnqueueThrow(new TaskCanceledException("timed out"));

        await Should.ThrowAsync<TaskCanceledException>(
            () => client.GetMyChannelAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Transport_Failure_Propagates_As_HttpRequestException()
    {
        var (client, handler) = Build();
        handler.EnqueueThrow(new HttpRequestException("connection reset"));

        await Should.ThrowAsync<HttpRequestException>(
            () => client.GetMyChannelAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Error_Messages_And_Logs_Never_Contain_The_Api_Key()
    {
        const string key = "AIzaSyTestOnlyNotARealKey000000000";
        var failures = new Queue<HttpResponseMessage>([
            StubHttpMessageHandler.Json(HttpStatusCode.BadRequest, GoogleJson.Error(400, "keyInvalid")),
            StubHttpMessageHandler.Json(HttpStatusCode.Unauthorized, GoogleJson.Error(401, "required")),
            StubHttpMessageHandler.Json(HttpStatusCode.Forbidden, GoogleJson.Error(403, "quotaExceeded")),
            StubHttpMessageHandler.Json(HttpStatusCode.Forbidden, GoogleJson.Error(403, "forbidden")),
            StubHttpMessageHandler.Json(HttpStatusCode.NotFound, GoogleJson.Error(404, "notFound")),
            StubHttpMessageHandler.Json(HttpStatusCode.InternalServerError, "{}"),
        ]);
        var stub = new StubHttpMessageHandler(_ => failures.Dequeue());
        var logs = new CapturingLoggerProvider();
        using var provider = YouTubeComposition.Compose(stub, apiKey: key, logs: logs);
        var client = provider.GetRequiredService<IYouTubeClient>();
        var messages = new List<string>();

        while (failures.Count > 0)
        {
            var ex = await Should.ThrowAsync<YouTubeApiException>(
                () => client.GetChannelByHandleAsync("ownerchannel", CancellationToken.None));
            messages.Add(ex.Message);
        }

        stub.Requests.Count.ShouldBe(6);
        stub.Requests.ShouldAllBe(r => r.Uri.Query.Contains("key=" + key));
        messages.ShouldAllBe(m => !m.Contains(key) && !m.Contains("googleapis"));
        logs.Lines.ShouldAllBe(line => !line.Contains(key) && !line.Contains("googleapis"));
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly List<string> _lines = [];

        public IReadOnlyList<string> Lines
        {
            get
            {
                lock (_lines)
                {
                    return [.. _lines];
                }
            }
        }

        public ILogger CreateLogger(string categoryName) => new Logger(this);

        public void Dispose()
        {
        }

        private sealed class Logger(CapturingLoggerProvider owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull
            {
                Add(state.ToString() ?? string.Empty);
                return null;
            }

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
                => Add(formatter(state, exception) + " " + exception);

            private void Add(string line)
            {
                lock (owner._lines)
                {
                    owner._lines.Add(line);
                }
            }
        }
    }
}
