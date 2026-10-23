using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Scout.Conversation.Abstractions;
using Scout.Conversation.Access;
using Scout.Conversation.Streaming;
using Scout.Conversation.Tests.Fakes;

namespace Scout.Conversation.Tests;

public sealed class ConversationConductorAccessTests
{
    private const string NotAllowedReply = "Scout only answers its configured users.";
    private const string NoAllowedUsersReply =
        "Scout has no allowed users yet. The owner can add them in the Scout desktop app under Settings.";

    private static readonly RenderCapabilities FineGrained = new()
    {
        FineGrainedEdit = true,
        MaxBodyLength = 4096,
    };

    private static StreamPump Pump() => new(new StreamPumpOptions
    {
        TimeProvider = new FakeTimeProvider(new DateTimeOffset(2026, 10, 23, 0, 0, 0, TimeSpan.Zero)),
    });

    private static async IAsyncEnumerable<string> Tokens(params string[] chunks)
    {
        foreach (var chunk in chunks)
        {
            await Task.Yield();
            yield return chunk;
        }
    }

    private static IConversationRouter AnsweringRouter()
    {
        var router = Substitute.For<IConversationRouter>();
        router.Route(Arg.Any<ChatMessage>()).Returns(_ => new ChatResponse(Tokens("hello")));
        return router;
    }

    private static ConversationConductor Conductor(
        IConversationRouter router,
        AccessOptions access,
        ILogger<ConversationConductor>? logger = null)
        => new(router, Pump(), logger, Options.Create(access));

    [Fact]
    public async Task User_Not_In_The_Allowed_List_Gets_The_Refusal_And_The_Router_Is_Never_Called()
    {
        var router = AnsweringRouter();
        var conductor = Conductor(router, new AccessOptions { AllowedUserIds = ["111"] });
        var sink = new RecordingResponseSink(FineGrained);

        await conductor.HandleAsync(new ChatMessage("222", "c1", "list my clusters"), sink, CancellationToken.None);

        sink.Completed.ShouldBeTrue();
        sink.FinalText.ShouldBe(NotAllowedReply);
        sink.Updates.ShouldBeEmpty();
        sink.FinalCitations.ShouldBeEmpty();
        router.DidNotReceive().Route(Arg.Any<ChatMessage>());
    }

    [Fact]
    public async Task User_In_The_Allowed_List_Is_Answered()
    {
        var router = AnsweringRouter();
        var conductor = Conductor(router, new AccessOptions { AllowedUserIds = ["111", "222"] });
        var sink = new RecordingResponseSink(FineGrained);

        await conductor.HandleAsync(new ChatMessage("222", "c1", "hi"), sink, CancellationToken.None);

        sink.FinalText.ShouldBe("hello");
        router.Received(1).Route(Arg.Is<ChatMessage>(m => m.UserId == "222"));
    }

    [Fact]
    public async Task Empty_List_With_An_Owner_Credential_Connected_Refuses_With_The_Setup_Text()
    {
        var router = AnsweringRouter();
        var conductor = Conductor(router, new AccessOptions { RequireAllowList = true });
        var sink = new RecordingResponseSink(FineGrained);

        await conductor.HandleAsync(new ChatMessage("111", "c1", "hi"), sink, CancellationToken.None);

        sink.Completed.ShouldBeTrue();
        sink.FinalText.ShouldBe(NoAllowedUsersReply);
        router.DidNotReceive().Route(Arg.Any<ChatMessage>());
    }

    [Fact]
    public async Task Empty_List_Without_Owner_Credentials_Answers_Everyone_As_Before()
    {
        var router = AnsweringRouter();
        var conductor = Conductor(router, new AccessOptions());
        var sink = new RecordingResponseSink(FineGrained);

        await conductor.HandleAsync(new ChatMessage("anyone", "c1", "hi"), sink, CancellationToken.None);

        sink.FinalText.ShouldBe("hello");
        router.Received(1).Route(Arg.Any<ChatMessage>());
    }

    [Theory]
    [InlineData(" Owner-1 ", "Owner-1", "hello")]
    [InlineData("Owner-1", " Owner-1\t", "hello")]
    [InlineData("Owner-1", "owner-1", NotAllowedReply)]
    public async Task Ids_Are_Compared_Ordinally_After_Trimming(
        string allowedId, string senderId, string expectedReply)
    {
        var conductor = Conductor(AnsweringRouter(), new AccessOptions { AllowedUserIds = [allowedId] });
        var sink = new RecordingResponseSink(FineGrained);

        await conductor.HandleAsync(new ChatMessage(senderId, "c1", "hi"), sink, CancellationToken.None);

        sink.FinalText.ShouldBe(expectedReply);
    }

    [Fact]
    public async Task Refusal_Logs_The_Channel_Id_And_Never_The_Message_Text()
    {
        const string text = "what is the prod admin token zq-7731";
        var logger = new RecordingLogger<ConversationConductor>();
        var conductor = Conductor(AnsweringRouter(), new AccessOptions { AllowedUserIds = ["111"] }, logger);

        await conductor.HandleAsync(
            new ChatMessage("222", "chan-42", text), new RecordingResponseSink(FineGrained), CancellationToken.None);

        var info = logger.Entries.ShouldHaveSingleItem();
        info.Level.ShouldBe(LogLevel.Information);
        info.Message.ShouldContain("chan-42");
        logger.Entries.ShouldAllBe(e => !e.Message.Contains("zq-7731") && !e.Message.Contains("222"));
    }
}
