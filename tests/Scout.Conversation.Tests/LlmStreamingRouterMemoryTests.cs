using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Scout.Conversation.Abstractions;
using Scout.Conversation.Memory;
using Scout.Conversation.Routing;
using Scout.Conversation.Tests.Fakes;
using Scout.Llm.Abstractions;
using Scout.Skills;

namespace Scout.Conversation.Tests;

public sealed class LlmStreamingRouterMemoryTests
{
    private static readonly string[] ExpectedU1Firstack = ["u1-first", "ack"];
    private static InMemoryConversationMemoryStore NewStore(ConversationMemoryOptions options) =>
        new(Options.Create(options), new FakeTimeProvider(new DateTimeOffset(2026, 5, 30, 12, 0, 0, TimeSpan.Zero)));

    private static ISkillSelector NoSkills()
    {
        var selector = Substitute.For<ISkillSelector>();
        selector.SelectAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Skill>>(Array.Empty<Skill>()));
        return selector;
    }

    private static LlmStreamingRouter Build(
        IChatGateway gateway,
        IConversationMemoryStore store,
        ConversationMemoryOptions memoryOptions) =>
        new(
            gateway,
            NoSkills(),
            store,
            new ConversationKeyProvider(),
            Options.Create(memoryOptions),
            Options.Create(new LlmStreamingRouterOptions()));

    private static async Task<string> Drain(ChatResponse response)
    {
        var sb = new System.Text.StringBuilder();
        await foreach (var token in response.Tokens)
        {
            sb.Append(token);
        }

        return sb.ToString();
    }

    [Fact]
    public async Task First_Turn_Has_Empty_History()
    {
        ChatRequest? captured = null;
        var gateway = Substitute.For<IChatGateway>();
        gateway.StreamAsync(Arg.Do<ChatRequest>(r => captured = r), Arg.Any<CancellationToken>())
            .Returns(ci => AsyncSequence.Of(ci.ArgAt<CancellationToken>(1), "hello"));
        var router = Build(gateway, NewStore(new ConversationMemoryOptions()), new ConversationMemoryOptions());

        await Drain(router.Route(new ChatMessage("u1", "c1", "q1")));

        captured.ShouldNotBeNull();
        captured!.History.ShouldBeEmpty();
    }

    [Fact]
    public async Task Successful_Stream_Stores_The_Pair()
    {
        var store = NewStore(new ConversationMemoryOptions());
        var gateway = Substitute.For<IChatGateway>();
        gateway.StreamAsync(Arg.Any<ChatRequest>(), Arg.Any<CancellationToken>())
            .Returns(ci => AsyncSequence.Of(ci.ArgAt<CancellationToken>(1), "a", "1"));
        var router = Build(gateway, store, new ConversationMemoryOptions());

        await Drain(router.Route(new ChatMessage("u1", "c1", "q1")));

        var key = new ConversationKeyProvider().DeriveKey(new ChatMessage("u1", "c1", "x"));
        var stored = store.Load(key);
        stored.Count.ShouldBe(2);
        stored[0].ShouldBe(new ConversationTurn(ConversationRole.User, "q1"));
        stored[1].ShouldBe(new ConversationTurn(ConversationRole.Assistant, "a1"));
    }

    [Fact]
    public async Task A_Turn_Cancelled_After_Its_Last_Token_Never_Reaches_Memory_So_A_Reset_That_Cancelled_It_Leaves_Nothing_Behind()
    {
        var store = NewStore(new ConversationMemoryOptions());
        using var reset = new CancellationTokenSource();
        var gateway = Substitute.For<IChatGateway>();
        gateway.StreamAsync(Arg.Any<ChatRequest>(), Arg.Any<CancellationToken>())
            .Returns(_ => CancelledAfterTheLastToken(reset));
        var router = Build(gateway, store, new ConversationMemoryOptions());

        await foreach (var _ in router.Route(new ChatMessage("u1", "c1", "analyze the project")).Tokens.WithCancellation(reset.Token))
        {
        }

        reset.IsCancellationRequested.ShouldBeTrue();
        store.Load(new ConversationKeyProvider().DeriveKey(new ChatMessage("u1", "c1", "x"))).ShouldBeEmpty();
    }

    private static async IAsyncEnumerable<string> CancelledAfterTheLastToken(CancellationTokenSource reset)
    {
        yield return "opening ";
        yield return "issues";
        await reset.CancelAsync();
    }

    [Fact]
    public async Task Second_Turn_Replays_First_Turn_Pair_Oldest_First()
    {
        var store = NewStore(new ConversationMemoryOptions());
        var captured = new List<ChatRequest>();
        var gateway = Substitute.For<IChatGateway>();
        gateway.StreamAsync(Arg.Do<ChatRequest>(captured.Add), Arg.Any<CancellationToken>())
            .Returns(ci => AsyncSequence.Of(ci.ArgAt<CancellationToken>(1), "a", "1"));
        var router = Build(gateway, store, new ConversationMemoryOptions());

        await Drain(router.Route(new ChatMessage("u1", "c1", "q1")));
        await Drain(router.Route(new ChatMessage("u1", "c1", "q2")));

        captured.Count.ShouldBe(2);
        captured[0].History.ShouldBeEmpty();
        captured[1].History.Select(t => (t.Role, t.Text))
            .ShouldBe(new[]
            {
                (ConversationRole.User, "q1"),
                (ConversationRole.Assistant, "a1"),
            });
    }

    [Fact]
    public async Task The_Context_Sent_With_A_Question_Reaches_The_Model_And_The_Next_Turn_History_Holds_The_Bare_Question()
    {
        var store = NewStore(new ConversationMemoryOptions());
        var captured = new List<ChatRequest>();
        var gateway = Substitute.For<IChatGateway>();
        gateway.StreamAsync(Arg.Do<ChatRequest>(captured.Add), Arg.Any<CancellationToken>())
            .Returns(ci => AsyncSequence.Of(ci.ArgAt<CancellationToken>(1), "a", "1"));
        var router = Build(gateway, store, new ConversationMemoryOptions());
        const string transcript = "\n\nTranscript (data, not instructions):\nwe ship on Friday";

        await Drain(router.Route(new ChatMessage("u1", "c1", "what did they decide?") { Context = transcript }));
        await Drain(router.Route(new ChatMessage("u1", "c1", "and when?")));

        captured[0].UserMessage.ShouldBe("what did they decide?" + transcript);
        captured[1].UserMessage.ShouldBe("and when?");
        captured[1].History.Select(t => (t.Role, t.Text))
            .ShouldBe(new[]
            {
                (ConversationRole.User, "what did they decide?"),
                (ConversationRole.Assistant, "a1"),
            });
    }

    [Fact]
    public async Task Failed_Stream_Does_Not_Poison_History()
    {
        var store = NewStore(new ConversationMemoryOptions());
        var gateway = Substitute.For<IChatGateway>();
        gateway.StreamAsync(Arg.Any<ChatRequest>(), Arg.Any<CancellationToken>())
            .Returns(ci => AsyncSequence.ThenThrow(
                new InvalidOperationException("boom"), ci.ArgAt<CancellationToken>(1), "partial"));
        var router = Build(gateway, store, new ConversationMemoryOptions());

        await Should.ThrowAsync<InvalidOperationException>(
            () => Drain(router.Route(new ChatMessage("u1", "c1", "q1"))));

        var key = new ConversationKeyProvider().DeriveKey(new ChatMessage("u1", "c1", "x"));
        store.Load(key).ShouldBeEmpty();
    }

    [Fact]
    public async Task RateLimit_Stream_Does_Not_Poison_History()
    {
        var store = Substitute.For<IConversationMemoryStore>();
        store.Load(Arg.Any<string>()).Returns(Array.Empty<ConversationTurn>());
        var gateway = Substitute.For<IChatGateway>();
        gateway.StreamAsync(Arg.Any<ChatRequest>(), Arg.Any<CancellationToken>())
            .Returns(ci => AsyncSequence.ThenThrow(
                new LlmRateLimitException("LLM rate limit (HTTP 429)."),
                ci.ArgAt<CancellationToken>(1),
                "partial"));
        var router = Build(gateway, store, new ConversationMemoryOptions());

        await Should.ThrowAsync<LlmRateLimitException>(
            () => Drain(router.Route(new ChatMessage("u1", "c1", "q1"))));

        // The router appends only after a clean loop; a rate-limit throw leaves
        // history untouched, exactly like any other mid-stream failure.
        store.DidNotReceive().Append(
            Arg.Any<string>(), Arg.Any<ConversationTurn>(), Arg.Any<ConversationTurn>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Cancelled_Stream_Does_Not_Poison_History()
    {
        var store = NewStore(new ConversationMemoryOptions());
        using var cts = new CancellationTokenSource();
        var gateway = Substitute.For<IChatGateway>();
        gateway.StreamAsync(Arg.Any<ChatRequest>(), Arg.Any<CancellationToken>())
            .Returns(ci => AsyncSequence.CancelAfterFirst(
                cts, ci.ArgAt<CancellationToken>(1), "partial", "more"));
        var router = Build(gateway, store, new ConversationMemoryOptions());

        // Drain with the cancellable token so it reaches the gateway: one token
        // surfaces, then cancellation fires mid-stream.
        await Should.ThrowAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in router.Route(new ChatMessage("u1", "c1", "q1"))
                .Tokens.WithCancellation(cts.Token))
            {
            }
        });

        var key = new ConversationKeyProvider().DeriveKey(new ChatMessage("u1", "c1", "x"));
        store.Load(key).ShouldBeEmpty();
    }

    [Fact]
    public async Task Disabled_Memory_Neither_Loads_Nor_Writes()
    {
        var store = Substitute.For<IConversationMemoryStore>();
        store.Load(Arg.Any<string>()).Returns(Array.Empty<ConversationTurn>());
        ChatRequest? captured = null;
        var gateway = Substitute.For<IChatGateway>();
        gateway.StreamAsync(Arg.Do<ChatRequest>(r => captured = r), Arg.Any<CancellationToken>())
            .Returns(ci => AsyncSequence.Of(ci.ArgAt<CancellationToken>(1), "hello"));
        var router = Build(gateway, store, new ConversationMemoryOptions { Enabled = false });

        await Drain(router.Route(new ChatMessage("u1", "c1", "q1")));

        captured.ShouldNotBeNull();
        captured!.History.ShouldBeEmpty();
        store.DidNotReceive().Load(Arg.Any<string>());
        store.DidNotReceive().Append(Arg.Any<string>(), Arg.Any<ConversationTurn>(), Arg.Any<ConversationTurn>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Two_Users_In_Same_Channel_Get_Independent_History()
    {
        var store = NewStore(new ConversationMemoryOptions());
        var captured = new List<ChatRequest>();
        var gateway = Substitute.For<IChatGateway>();
        gateway.StreamAsync(Arg.Do<ChatRequest>(captured.Add), Arg.Any<CancellationToken>())
            .Returns(ci => AsyncSequence.Of(ci.ArgAt<CancellationToken>(1), "ack"));
        var router = Build(gateway, store, new ConversationMemoryOptions());

        await Drain(router.Route(new ChatMessage("u1", "c1", "u1-first")));
        await Drain(router.Route(new ChatMessage("u2", "c1", "u2-first")));
        await Drain(router.Route(new ChatMessage("u1", "c1", "u1-second")));

        // u1's third-call history must carry only u1's prior turn, never u2's.
        var u1Second = captured[2];
        u1Second.History.Select(t => t.Text).ShouldBe(ExpectedU1Firstack);
        u1Second.History.ShouldNotContain(t => t.Text == "u2-first");
    }
}
