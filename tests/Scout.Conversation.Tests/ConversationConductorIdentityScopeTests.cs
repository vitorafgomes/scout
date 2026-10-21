using System.Runtime.CompilerServices;
using Microsoft.Extensions.Time.Testing;
using Scout.Contracts;
using Scout.Conversation.Abstractions;
using Scout.Conversation.Streaming;
using Scout.Conversation.Tests.Fakes;

namespace Scout.Conversation.Tests;

/// <summary>
/// STEP 0 lifetime proof for SelfInfo Path B. The load-bearing assumption is that
/// the identity opened by <see cref="ConversationConductor.HandleAsync"/> via
/// <see cref="IScoutRequestContext.BeginScope"/> is visible to code that runs
/// DURING the streaming enumeration — which is exactly where Semantic Kernel
/// invokes plugin functions. Here a fake router reads
/// <see cref="IScoutRequestContext.Current"/> at ENUMERATION TIME (not at
/// <c>Route</c> time), and the assertion is that the observed identity equals the
/// one derived from the inbound <see cref="ChatMessage"/>. The stream is driven
/// through the real <see cref="StreamPump"/>, whose <c>ConfigureAwait(false)</c>
/// enumeration proves that flag does not drop the ambient value. Finally,
/// <see cref="IScoutRequestContext.Current"/> must be null again after the call.
/// </summary>
public sealed class ConversationConductorIdentityScopeTests
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

    // Reads the ambient identity AT ENUMERATION TIME, mirroring where the kernel
    // invokes a plugin function while the LLM response streams. The read sits
    // behind ConfigureAwait(false) awaits so the test also proves that flag does
    // not sever the ExecutionContext-carried value.
    private sealed class IdentityRecordingRouter(IScoutRequestContext context) : IConversationRouter
    {
        public ScoutRequestIdentity? ObservedAtEnumeration { get; private set; }
        public bool Enumerated { get; private set; }

        public ChatResponse Route(ChatMessage message) => new(Stream());

        private async IAsyncEnumerable<string> Stream(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            await Task.Delay(1, cancellationToken).ConfigureAwait(false);

            ObservedAtEnumeration = context.Current;
            Enumerated = true;

            yield return "token";
        }
    }

    [Fact]
    public async Task Identity_Set_At_Choke_Point_Is_Visible_During_Streaming_Enumeration()
    {
        var context = new AsyncLocalScoutRequestContext();
        var router = new IdentityRecordingRouter(context);
        var conductor = new ConversationConductor(router, Pump(), context);
        var sink = new RecordingResponseSink(FineGrained);

        var message = new ChatMessage(
            UserId: "user-42",
            ChannelId: "channel-7",
            Text: "where are you installed?",
            IsDirectMessage: true,
            AdapterName: "Discord");

        await conductor.HandleAsync(message, sink, CancellationToken.None);

        router.Enumerated.ShouldBeTrue("the router stream must have been enumerated inside the scope");
        router.ObservedAtEnumeration.ShouldNotBeNull();
        router.ObservedAtEnumeration!.UserId.ShouldBe("user-42");
        router.ObservedAtEnumeration.ChannelId.ShouldBe("channel-7");
        router.ObservedAtEnumeration.IsDirectMessage.ShouldBeTrue();
        router.ObservedAtEnumeration.AdapterName.ShouldBe("Discord");

        // The response completed successfully, proving the enumeration ran to term.
        sink.Completed.ShouldBeTrue();
        sink.FinalText.ShouldBe("token");
    }

    [Fact]
    public async Task Current_Is_Null_Again_After_HandleAsync_Returns()
    {
        var context = new AsyncLocalScoutRequestContext();
        var router = new IdentityRecordingRouter(context);
        var conductor = new ConversationConductor(router, Pump(), context);
        var sink = new RecordingResponseSink(FineGrained);

        context.Current.ShouldBeNull();

        await conductor.HandleAsync(
            new ChatMessage("u1", "c1", "hi", IsDirectMessage: false, AdapterName: "Slack"),
            sink,
            CancellationToken.None);

        context.Current.ShouldBeNull();
    }
}
