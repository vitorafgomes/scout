using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Scout.Plugin.YouTube.Abstractions;

namespace Scout.Operator.Tests.Fakes;

/// <summary>Builders for the operator's inputs and canned model answers.</summary>
internal static class Given
{
    public static readonly DateTimeOffset Now = new(2026, 11, 3, 12, 0, 0, TimeSpan.Zero);

    public static OperatorMandate Mandate(
        IReadOnlyList<string>? pillars = null,
        IReadOnlyList<string>? blocked = null,
        string language = "English")
        => new(
            "Teach home baking",
            "Beginner bakers",
            pillars ?? [],
            "weekly",
            language,
            "warm and precise",
            blocked ?? [],
            "views",
            "10000");

    public static OperatorProjectState Project(string id = "p1", string topic = "sourdough", string status = "researching")
        => new(id, topic, status, "chat", "2026-11-03T12:00:00Z", "2026-11-03T12:00:00Z", [], null, []);

    public static YouTubeChannel Channel()
        => new("UCowner", "Owner Bakes", "@ownerbakes", 1000, 50000, 2, Now.AddYears(-3), "UUowner");

    public static YouTubeVideo Upload(string id = "upload00001", string title = "My first loaf")
        => new(id, title, "Owner Bakes", Now.AddDays(-7), TimeSpan.FromMinutes(8), 1200, 80, 9);

    public static YouTubeSearchResult Found(string id = "found000001", string title = "Sourdough in 10 minutes")
        => new(id, title, "Bread Lab", Now.AddDays(-30), "A fast starter.");

    public static IYouTubeClient YouTube(
        IReadOnlyList<YouTubeVideo>? uploads = null,
        IReadOnlyList<YouTubeSearchResult>? found = null)
    {
        var youtube = Substitute.For<IYouTubeClient>();
        youtube.GetMyChannelAsync(Arg.Any<CancellationToken>()).Returns(Channel());
        youtube.ListUploadsAsync("UUowner", Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(uploads ?? [Upload()]);
        youtube.SearchVideosAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(found ?? [Found()]);
        return youtube;
    }

    public static SearchBudget Budget() => new(new FakeTimeProvider(Now));

    public static string Candidates(params string[] titles)
        => "{\"candidates\":[" + string.Join(",", titles.Select(t => $"{{\"title\":\"{t}\",\"reason\":\"fits\",\"fit\":0.8}}")) + "]}";

    public static string Script(string claims = "[]")
        => "{\"hook\":\"Bread in ten minutes?\",\"sections\":[{\"heading\":\"Starter\",\"narration\":\"Feed it daily.\"}]," +
           "\"callToAction\":\"Subscribe\",\"titleVariants\":[\"A\",\"B\",\"C\"],\"description\":\"How to bake.\"," +
           "\"tags\":[\"bread\"],\"chapters\":[{\"at\":\"0:00\",\"title\":\"Intro\"}],\"claims\":" + claims + "}";

    public static TopicResearcher Researcher(IYouTubeClient youtube, ScriptedChat chat, SearchBudget? budget = null)
        => new(youtube, chat, budget ?? Budget());

    public static OperatorPipeline Pipeline(
        IOperatorControlPlane controlPlane,
        IYouTubeClient youtube,
        ScriptedChat chat,
        Scout.Conversation.Abstractions.IOriginNotifier notifier)
        => new(
            controlPlane,
            Researcher(youtube, chat),
            new ScriptWriter(chat),
            notifier,
            NullLogger<OperatorPipeline>.Instance);
}
