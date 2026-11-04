using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Microsoft.SemanticKernel.ChatCompletion;
using Scout.Operator.Tests.Fakes;
using Scout.Plugin.YouTube;
using Scout.Plugin.YouTube.Abstractions;

namespace Scout.Operator.Tests;

public sealed class CommentRequestsTests
{
    private readonly FakeTimeProvider _time = new(Given.Now);
    private readonly IYouTubeClient _youtube = Substitute.For<IYouTubeClient>();
    private readonly RecordingControlPlane _controlPlane = new();
    private readonly RecordingLogger<CommentRequests> _logs = new();

    public CommentRequestsTests()
    {
        _youtube.GetMyChannelAsync(Arg.Any<CancellationToken>()).Returns(Given.Channel());
        _youtube.ListCommentThreadsAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new YouTubeCommentThreadPage([], null));
    }

    private static OperatorComment Requested(string id)
        => new(id, $"thread-{id}", "video000001", "Bread", "Viewer", "Nice loaf", "2026-11-03T10:00:00.0000000Z", 0, "praise", "thanks",
            "draft_requested", string.Empty, null, null, null, "2026-11-03T11:00:00.1234567+00:00", "2026-11-03T10:05:00.0000000+00:00");

    private CommentRequests Build(ScriptedChat chat)
        => new(_controlPlane, new CommentDesk(_controlPlane, _youtube, chat, _time, new RecordingLogger<CommentDesk>()), _time, _logs);

    [Fact]
    public async Task A_Sync_Request_Runs_The_Desk_Within_A_Minute()
    {
        var requests = Build(ScriptedChat.Answering("""{"comments":[]}"""));
        await requests.StartAsync(CancellationToken.None);
        await WaitAsync(() => _controlPlane.CommentReads == 1);
        var beforeTheRequest = (_controlPlane.CommentSaves.Count, _youtube.ReceivedCalls().Count());

        _controlPlane.Comments = _controlPlane.Comments with { SyncRequestedAt = "2026-11-03T12:00:10.1234567+00:00" };
        _time.Advance(TimeSpan.FromMinutes(1));
        await WaitAsync(() => _controlPlane.CommentSaves.Count == 1);
        await requests.StopAsync(CancellationToken.None);

        beforeTheRequest.ShouldBe((0, 0));
        _controlPlane.Comments.SyncRequestedAt.ShouldBeNull();
        _controlPlane.Comments.SyncedAt.ShouldBe("2026-11-03T12:01:00.0000000Z");
        await _youtube.Received(1).ListCommentThreadsAsync("UCowner", null, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_Draft_Request_Runs_Only_The_Drafts_And_A_Failed_One_Is_Not_Asked_Again_Until_The_Requests_Change()
    {
        _controlPlane.Comments = new([Requested("c1")], [], null, null, null);
        var chat = new ScriptedChat(() => throw new HttpRequestException("connection refused"));
        var requests = Build(chat);

        await requests.RunOnceAsync(CancellationToken.None);
        await requests.RunOnceAsync(CancellationToken.None);
        _controlPlane.Comments = _controlPlane.Comments with { Comments = [Requested("c1"), Requested("c2")] };
        await requests.RunOnceAsync(CancellationToken.None);

        chat.Requests.Count.ShouldBe(2);
        _controlPlane.CommentSaves.ShouldBeEmpty();
        _youtube.ReceivedCalls().ShouldBeEmpty();
    }

    [Fact]
    public async Task A_Draft_Request_The_Model_Left_Unanswered_Is_Not_Asked_Again_Every_Minute()
    {
        _controlPlane.Comments = new([Requested("c1")], [], null, null, null);
        var chat = ScriptedChat.Answering("""{"replies":[{"id":"c9","reply":"Thanks!"}]}""");
        var requests = Build(chat);

        await requests.RunOnceAsync(CancellationToken.None);
        await requests.RunOnceAsync(CancellationToken.None);

        chat.Requests.Count.ShouldBe(1);
        _controlPlane.Comments.Comments.ShouldHaveSingleItem().Status.ShouldBe("draft_requested");
    }

    [Fact]
    public async Task A_Sync_Whose_Save_Fails_Is_Not_Run_Again_Every_Minute()
    {
        var down = new RecordingControlPlane { CommentSaveFailure = _ => new ControlPlaneUnavailableException("Scout could not reach its control plane.") };
        down.Comments = new([], [], null, "2026-11-03T11:59:00.1234567+00:00", null);
        var requests = new CommentRequests(down, new CommentDesk(down, _youtube, ScriptedChat.Answering("{}"), _time, new RecordingLogger<CommentDesk>()), _time, _logs);

        await requests.RunOnceAsync(CancellationToken.None);
        await requests.RunOnceAsync(CancellationToken.None);
        var beforeTheNextClick = _youtube.ReceivedCalls().Count(c => c.GetMethodInfo().Name == nameof(IYouTubeClient.ListCommentThreadsAsync));
        down.Comments = down.Comments with { SyncRequestedAt = "2026-11-03T12:05:00.0000000+00:00" };
        await requests.RunOnceAsync(CancellationToken.None);

        beforeTheNextClick.ShouldBe(1);
        _youtube.ReceivedCalls().Count(c => c.GetMethodInfo().Name == nameof(IYouTubeClient.ListCommentThreadsAsync)).ShouldBe(2);
        down.CommentSaves.Count.ShouldBe(2);
        _logs.Entries.ShouldBe([
            (LogLevel.Warning, "The operator could not answer a comment request: Scout could not reach its control plane."),
            (LogLevel.Warning, "The operator could not answer a comment request: Scout could not reach its control plane."),
        ]);
    }

    [Fact]
    public async Task A_Control_Plane_That_Cannot_Answer_Is_Logged_Once_Until_It_Answers()
    {
        _controlPlane.CommentsFailure = new ControlPlaneUnavailableException("Scout could not reach its control plane.");
        var chat = ScriptedChat.Answering("""{"comments":[]}""");
        var requests = Build(chat);

        await requests.RunOnceAsync(CancellationToken.None);
        await requests.RunOnceAsync(CancellationToken.None);
        _controlPlane.CommentsFailure = null;
        await requests.RunOnceAsync(CancellationToken.None);

        _logs.Entries.ShouldHaveSingleItem().ShouldBe((LogLevel.Warning, "The operator cannot answer the comment requests: Scout could not reach its control plane."));
        (chat.Requests.Count, _controlPlane.CommentSaves.Count).ShouldBe((0, 0));
    }

    [Fact]
    public async Task A_Key_Only_Setup_Answers_A_Sync_Request_With_The_Sign_In_Sentence()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Scout:Plugins:YouTube:ApiKey"] = "key-only" })
            .Build();
        _controlPlane.Comments = new([], [], null, "2026-11-03T11:59:00.1234567+00:00", null);
        var youtube = new RecordingHandler(request => new HttpResponseMessage(System.Net.HttpStatusCode.Unauthorized));
        var services = Composed(configuration, youtube);
        using var provider = services.BuildServiceProvider();

        services.Any(d => d.ImplementationType == typeof(CommentRequests)).ShouldBeTrue();
        services.Any(d => d.ServiceType == typeof(IYouTubeWriteClient) || d.ImplementationType == typeof(ActionExecutor)).ShouldBeFalse();
        await ActivatorUtilities.CreateInstance<CommentRequests>(provider).RunOnceAsync(CancellationToken.None);

        (_controlPlane.Comments.SyncError, _controlPlane.Comments.SyncRequestedAt).ShouldBe(
            ("Sign in from the Scout app to read your own channel; an API key reads public channels by handle only.", null));
        youtube.Requests.ShouldHaveSingleItem().ShouldStartWith("/youtube/v3/channels?part=snippet,statistics,contentDetails&mine=true&key=");
    }

    [Fact]
    public async Task A_Read_Only_Sign_In_Answers_A_Sync_Request_With_The_Allow_Writes_Sentence()
    {
        var tokenFile = Path.Combine(Directory.CreateTempSubdirectory("scout-read-only-").FullName, "youtube-token.json");
        File.WriteAllText(tokenFile, """{"access_token":"ya29.read_only"}""");
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Scout:Plugins:YouTube:TokenFile"] = tokenFile })
            .Build();
        _controlPlane.Comments = new([], [], null, "2026-11-03T11:59:00.1234567+00:00", null);
        var youtube = new RecordingHandler(request => request.RequestUri!.AbsolutePath.EndsWith("/channels", StringComparison.Ordinal)
            ? Json(System.Net.HttpStatusCode.OK, """{"items":[{"id":"UCowner","snippet":{"title":"Owner"},"contentDetails":{"relatedPlaylists":{"uploads":"UUowner"}}}]}""")
            : Json(System.Net.HttpStatusCode.Forbidden, """{"error":{"code":403,"errors":[{"domain":"global","reason":"insufficientPermissions"}]}}"""));
        using var provider = Composed(configuration, youtube).BuildServiceProvider();

        await ActivatorUtilities.CreateInstance<CommentRequests>(provider).RunOnceAsync(CancellationToken.None);

        (_controlPlane.Comments.SyncError, _controlPlane.Comments.SyncRequestedAt).ShouldBe((
            "Reading comments needs Scout's permission to reply to and moderate comments: open the YouTube card in Scout Desktop and allow writes again.",
            null));
        youtube.Requests.Select(r => r.Split('?')[0]).ShouldBe(["/youtube/v3/channels", "/youtube/v3/commentThreads"]);
        Directory.Delete(Path.GetDirectoryName(tokenFile)!, recursive: true);
    }

    private ServiceCollection Composed(IConfiguration configuration, RecordingHandler youtube)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IOperatorControlPlane>(_controlPlane);
        services.AddSingleton<TimeProvider>(_time);
        services.AddSingleton(Substitute.For<IChatCompletionService>());
        services.AddYouTubePlugin(configuration);
        services.AddHttpClient(nameof(IYouTubeClient)).ConfigurePrimaryHttpMessageHandler(() => youtube);
        services.AddYouTubeOperator();
        return services;
    }

    private static HttpResponseMessage Json(System.Net.HttpStatusCode status, string body)
        => new(status) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!.PathAndQuery);
            var response = respond(request);
            response.RequestMessage = request;
            return Task.FromResult(response);
        }
    }

    private static async Task WaitAsync(Func<bool> done)
    {
        for (var i = 0; i < 200 && !done(); i++)
        {
            await Task.Delay(10);
        }

        done().ShouldBeTrue();
    }
}
