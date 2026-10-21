using Microsoft.Extensions.Options;
using Scout.Conversation.Abstractions;
using Scout.Conversation.Memory;
using Scout.Conversation.Routing;
using Scout.Llm.Abstractions;
using Scout.Skills;

namespace Scout.Conversation.Tests.Routing;

public sealed class LlmStreamingRouterTests
{
    private static async IAsyncEnumerable<string> Tokens(params string[] chunks)
    {
        foreach (var chunk in chunks)
        {
            await Task.Yield();
            yield return chunk;
        }
    }

    private static ISkillSelector NoSkills()
    {
        var selector = Substitute.For<ISkillSelector>();
        selector.SelectAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Skill>>([]));
        return selector;
    }

    private static ISkillSelector WithSkill(Skill skill)
    {
        var selector = Substitute.For<ISkillSelector>();
        selector.SelectAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Skill>>([skill]));
        return selector;
    }

    private static IConversationMemoryStore NoMemory() => Substitute.For<IConversationMemoryStore>();

    private static LlmStreamingRouter Router(
        IChatGateway gateway,
        ISkillSelector? skillSelector = null,
        LlmStreamingRouterOptions? options = null)
        => new(
            gateway,
            skillSelector ?? NoSkills(),
            NoMemory(),
            new ConversationKeyProvider(),
            // Disabled keeps these legacy tests behavior-neutral (no load/write-back).
            Options.Create(new ConversationMemoryOptions { Enabled = false }),
            Options.Create(options ?? new LlmStreamingRouterOptions
            {
                SystemPrompt = "sys",
                Temperature = 0.3,
                MaxTokens = 100,
            }));

    [Fact]
    public void Ctor_Throws_When_Gateway_Null()
    {
        Should.Throw<ArgumentNullException>(
            () => new LlmStreamingRouter(
                null!, NoSkills(), NoMemory(), new ConversationKeyProvider(),
                Options.Create(new ConversationMemoryOptions()),
                Options.Create(new LlmStreamingRouterOptions())));
    }

    [Fact]
    public void Ctor_Throws_When_SkillSelector_Null()
    {
        var gateway = Substitute.For<IChatGateway>();
        Should.Throw<ArgumentNullException>(
            () => new LlmStreamingRouter(
                gateway, null!, NoMemory(), new ConversationKeyProvider(),
                Options.Create(new ConversationMemoryOptions()),
                Options.Create(new LlmStreamingRouterOptions())));
    }

    [Fact]
    public void Ctor_Throws_When_MemoryStore_Null()
    {
        var gateway = Substitute.For<IChatGateway>();
        Should.Throw<ArgumentNullException>(
            () => new LlmStreamingRouter(
                gateway, NoSkills(), null!, new ConversationKeyProvider(),
                Options.Create(new ConversationMemoryOptions()),
                Options.Create(new LlmStreamingRouterOptions())));
    }

    [Fact]
    public void Ctor_Throws_When_KeyProvider_Null()
    {
        var gateway = Substitute.For<IChatGateway>();
        Should.Throw<ArgumentNullException>(
            () => new LlmStreamingRouter(
                gateway, NoSkills(), NoMemory(), null!,
                Options.Create(new ConversationMemoryOptions()),
                Options.Create(new LlmStreamingRouterOptions())));
    }

    [Fact]
    public void Route_Throws_When_Message_Null()
    {
        var gateway = Substitute.For<IChatGateway>();
        Should.Throw<ArgumentNullException>(() => Router(gateway).Route(null!));
    }

    [Fact]
    public async Task Route_Maps_Message_To_Request_And_Forwards_Tokens()
    {
        var gateway = Substitute.For<IChatGateway>();
        ChatRequest? captured = null;
        gateway.StreamAsync(Arg.Any<ChatRequest>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                captured = (ChatRequest)call[0];
                return Tokens("one", "two");
            });

        var message = new ChatMessage("u1", "c1", "what is up");
        var response = Router(gateway).Route(message);

        var collected = new List<string>();
        await foreach (var token in response.Tokens)
        {
            collected.Add(token);
        }

        collected.ShouldBe(["one", "two"]);
        captured.ShouldNotBeNull();
        captured!.UserMessage.ShouldBe("what is up");
        captured.SystemPrompt.ShouldBe("sys");
        captured.Temperature.ShouldBe(0.3);
        captured.MaxTokens.ShouldBe(100);
        response.Citations.ShouldBeEmpty();
    }

    [Fact]
    public async Task Route_Injects_Selected_Skill_Body_Into_SystemPrompt()
    {
        var gateway = Substitute.For<IChatGateway>();
        ChatRequest? captured = null;
        gateway.StreamAsync(Arg.Any<ChatRequest>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                captured = (ChatRequest)call[0];
                return Tokens("ok");
            });

        var skill = new Skill("debugging", "use this when debugging", "isolate the fault first", "/skills/debugging.md");
        var response = Router(gateway, WithSkill(skill)).Route(new ChatMessage("u1", "c1", "help me debug"));

        await foreach (var _ in response.Tokens)
        {
        }

        captured.ShouldNotBeNull();
        captured!.SystemPrompt.ShouldStartWith("sys");
        captured.SystemPrompt.ShouldContain("# Relevant skill: debugging");
        captured.SystemPrompt.ShouldContain("isolate the fault first");
    }

    [Fact]
    public async Task Route_Leaves_SystemPrompt_Unchanged_When_No_Skill_Selected()
    {
        var gateway = Substitute.For<IChatGateway>();
        ChatRequest? captured = null;
        gateway.StreamAsync(Arg.Any<ChatRequest>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                captured = (ChatRequest)call[0];
                return Tokens("ok");
            });

        var response = Router(gateway, NoSkills()).Route(new ChatMessage("u1", "c1", "hello"));

        await foreach (var _ in response.Tokens)
        {
        }

        captured.ShouldNotBeNull();
        captured!.SystemPrompt.ShouldBe("sys");
    }

    [Fact]
    public void Route_Does_Not_Invoke_Gateway_Until_Enumerated()
    {
        var gateway = Substitute.For<IChatGateway>();
        gateway.StreamAsync(Arg.Any<ChatRequest>(), Arg.Any<CancellationToken>())
            .Returns(Tokens("x"));
        var selector = WithSkill(new Skill("s", "d", "b", "/s.md"));

        _ = Router(gateway, selector).Route(new ChatMessage("u1", "c1", "hi"));

        gateway.DidNotReceive().StreamAsync(Arg.Any<ChatRequest>(), Arg.Any<CancellationToken>());
        // Selection is also deferred to enumeration; nothing runs at Route time.
        selector.DidNotReceive().SelectAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }
}
