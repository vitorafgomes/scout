using Microsoft.Extensions.Time.Testing;
using Scout.Conversation.Abstractions;
using Scout.Conversation.Streaming;
using Scout.Conversation.Tests.Fakes;
using Scout.Llm.Abstractions;

namespace Scout.Conversation.Tests;

public sealed class ConversationConductorTests
{
    private static readonly RenderCapabilities FineGrained = new()
    {
        FineGrainedEdit = true,
        MaxBodyLength = 4096,
    };

    private static StreamPump Pump() => new(new StreamPumpOptions
    {
        TimeProvider = new FakeTimeProvider(new DateTimeOffset(2026, 5, 28, 0, 0, 0, TimeSpan.Zero)),
    });

    private static async IAsyncEnumerable<string> Tokens(params string[] chunks)
    {
        foreach (var chunk in chunks)
        {
            await Task.Yield();
            yield return chunk;
        }
    }

    [Fact]
    public async Task HandleAsync_Routes_And_Pumps_To_Sink()
    {
        var router = Substitute.For<IConversationRouter>();
        router.Route(Arg.Any<ChatMessage>())
            .Returns(new ChatResponse(Tokens("hello")));
        var conductor = new ConversationConductor(router, Pump());
        var sink = new RecordingResponseSink(FineGrained);

        await conductor.HandleAsync(
            new ChatMessage("u1", "c1", "hi"), sink, CancellationToken.None);

        sink.Completed.ShouldBeTrue();
        sink.FinalText.ShouldBe("hello");
    }

    [Fact]
    public async Task What_A_Function_Reports_During_The_Turn_Reaches_The_Sink_And_Stops_With_It()
    {
        var router = Substitute.For<IConversationRouter>();
        router.Route(Arg.Any<ChatMessage>()).Returns(new ChatResponse(Reporting("Reading pull request file src/Checkout.cs", "hello")));
        var conductor = new ConversationConductor(router, Pump());
        var sink = new RecordingResponseSink(FineGrained);

        await conductor.HandleAsync(new ChatMessage("u1", "c1", "hi"), sink, CancellationToken.None);
        await TurnProgress.ReportAsync("after the turn", CancellationToken.None);

        sink.Progress.ShouldBe(["Reading pull request file src/Checkout.cs"]);
        sink.FinalText.ShouldBe("hello");
        TurnProgress.IsEntered.ShouldBeFalse();
    }

    [Fact]
    public async Task A_Block_A_Function_Attaches_During_The_Turn_Ends_The_Reply_And_Stops_With_The_Turn()
    {
        var router = Substitute.For<IConversationRouter>();
        router.Route(Arg.Any<ChatMessage>()).Returns(new ChatResponse(Attaching("```scout-findings\n{}\n```", "Verdict: ready.")));
        var conductor = new ConversationConductor(router, Pump());
        var sink = new RecordingResponseSink(FineGrained);

        await conductor.HandleAsync(new ChatMessage("u1", "c1", "review PR 5"), sink, CancellationToken.None);
        TurnAttachments.Attach("```after\n{}\n```");

        sink.FinalText.ShouldBe("Verdict: ready.\n\n```scout-findings\n{}\n```");
        TurnAttachments.Take().ShouldBeEmpty();
    }

    private static async IAsyncEnumerable<string> Attaching(string block, string chunk)
    {
        await Task.Yield();
        TurnAttachments.Attach(block);
        yield return chunk;
    }

    private static async IAsyncEnumerable<string> Reporting(string activity, string chunk)
    {
        await TurnProgress.ReportAsync(activity, CancellationToken.None);
        yield return chunk;
    }

    [Fact]
    public async Task HandleAsync_Router_Throws_Surfaces_As_FailAsync_Not_Host_Crash()
    {
        var router = Substitute.For<IConversationRouter>();
        router.Route(Arg.Any<ChatMessage>())
            .Returns(_ => throw new InvalidOperationException("router boom"));
        var conductor = new ConversationConductor(router, Pump());
        var sink = new RecordingResponseSink(FineGrained);

        await Should.NotThrowAsync(() => conductor.HandleAsync(
            new ChatMessage("u1", "c1", "hi"), sink, CancellationToken.None));

        sink.Failed.ShouldBeTrue();
        sink.FailReason.ShouldNotBeNull();
        sink.FailReason!.ShouldContain("router boom");
        sink.Completed.ShouldBeFalse();
    }

    [Fact]
    public async Task A_Sink_Whose_Begin_And_Fail_Both_Throw_Never_Crashes_The_Host()
    {
        var router = Substitute.For<IConversationRouter>();
        router.Route(Arg.Any<ChatMessage>()).Returns(_ => new ChatResponse(Tokens("unused")));
        var conductor = new ConversationConductor(router, Pump());
        var sink = Substitute.For<IResponseSink>();
        sink.Capabilities.Returns(FineGrained);
        sink.BeginAsync(Arg.Any<CancellationToken>()).Returns(Task.FromException(new HttpRequestException("reset")));
        sink.FailAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("Terminal call before a successful BeginAsync.")));

        await Should.NotThrowAsync(() => conductor.HandleAsync(new ChatMessage("u1", "c1", "hi"), sink, CancellationToken.None));

        await sink.Received(1).FailAsync(string.Empty, "reset", CancellationToken.None);
    }

    [Fact]
    public async Task HandleAsync_Throws_When_Message_Null()
    {
        var conductor = new ConversationConductor(
            Substitute.For<IConversationRouter>(), Pump());
        var sink = new RecordingResponseSink(FineGrained);

        await Should.ThrowAsync<ArgumentNullException>(
            () => conductor.HandleAsync(null!, sink, CancellationToken.None));
    }

    [Fact]
    public void Ctor_Throws_When_Router_Null()
    {
        Should.Throw<ArgumentNullException>(() => new ConversationConductor(null!, Pump()));
    }
}
