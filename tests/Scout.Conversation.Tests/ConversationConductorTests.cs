using Microsoft.Extensions.Time.Testing;
using Scout.Conversation.Abstractions;
using Scout.Conversation.Streaming;
using Scout.Conversation.Tests.Fakes;

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
