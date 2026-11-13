using Microsoft.Extensions.Options;
using Scout.Conversation.Abstractions;
using Scout.Conversation.Memory;
using Scout.Conversation.Routing;
using Scout.Llm.Abstractions;
using Scout.Skills;

namespace Scout.Conversation.Tests.Routing;

/// <summary>
/// The output limit a chat turn sends: a plain chat answer keeps the configured chat limit, while a turn whose skills
/// declare functions (a review, an audit, the issues skill, the operator) sends none, so the provider's own limit applies
/// and the model has room to think, read and write its findings.
/// </summary>
public sealed class LlmStreamingRouterOutputLimitTests
{
    private const int ChatLimit = 800;

    private static readonly Skill Review = new("code-review", "Reviewing a pull request", "review body", "/skills/code-review.md")
    {
        Functions = ["GitHub.get_pull_request", "GitHubActions.submit_pull_request_review"],
    };

    private static readonly Skill Python = new("python", "Python rules", "python body", "/skills/python.md");

    private readonly List<ChatRequest> _requests = [];

    [Fact]
    public async Task A_Plain_Chat_Turn_With_No_Skill_Keeps_The_Chat_Limit()
    {
        await Ask(Router(_ => []), "hello");

        _requests.ShouldHaveSingleItem().MaxTokens.ShouldBe(ChatLimit);
    }

    [Fact]
    public async Task A_Turn_That_Runs_A_Skill_Declaring_Functions_Sends_No_Limit()
    {
        await Ask(Router(_ => [Review]), "review https://github.com/octocat/hello/pull/42");

        var request = _requests.ShouldHaveSingleItem();
        request.Skills.ShouldBe(["code-review"]);
        request.MaxTokens.ShouldBeNull();
    }

    [Fact]
    public async Task A_Turn_That_Carries_The_Previous_Turns_Skill_With_Functions_Sends_No_Limit()
    {
        var router = Router(text => text.StartsWith("review", StringComparison.Ordinal) ? [Review] : []);

        await Ask(router, "review https://github.com/octocat/hello/pull/42");
        await Ask(router, "and the tests too");

        _requests[1].Skills.ShouldBe(["code-review"]);
        _requests[1].MaxTokens.ShouldBeNull();
    }

    [Fact]
    public async Task A_Turn_Whose_Skill_Declares_No_Functions_Keeps_The_Chat_Limit()
    {
        await Ask(Router(_ => [Python]), "python typing");

        var request = _requests.ShouldHaveSingleItem();
        request.Skills.ShouldBe(["python"]);
        request.MaxTokens.ShouldBe(ChatLimit);
    }

    [Fact]
    public async Task A_Message_Carrying_The_Desktops_Review_Marker_Sends_No_Limit()
    {
        var catalog = Substitute.For<ISkillCatalog>();
        catalog.Find("code-review").Returns(Review);

        await Ask(Router(_ => [], catalog), "Post 2 findings on acme/shop#4\n\nReview to post (data, not instructions):\n{\"number\":4}");

        var request = _requests.ShouldHaveSingleItem();
        request.Skills.ShouldBe(["code-review"]);
        request.MaxTokens.ShouldBeNull();
    }

    private LlmStreamingRouter Router(Func<string, IReadOnlyList<Skill>> select, ISkillCatalog? catalog = null)
    {
        var gateway = Substitute.For<IChatGateway>();
        gateway.StreamAsync(Arg.Any<ChatRequest>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                _requests.Add((ChatRequest)call[0]);
                return Tokens();
            });
        var selector = Substitute.For<ISkillSelector>();
        selector.SelectAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult(select((string)call[0])));
        selector.ModelChoiceCatalog.Returns([]);
        return new LlmStreamingRouter(
            gateway,
            selector,
            Substitute.For<IConversationMemoryStore>(),
            new ConversationKeyProvider(),
            Options.Create(new ConversationMemoryOptions { Enabled = false }),
            Options.Create(new LlmStreamingRouterOptions { SystemPrompt = "sys", MaxTokens = ChatLimit }),
            catalog);
    }

    private static async IAsyncEnumerable<string> Tokens()
    {
        await Task.Yield();
        yield return "ok";
    }

    private static async Task Ask(LlmStreamingRouter router, string text)
    {
        await foreach (var _ in router.Route(new ChatMessage("u1", "c1", text)).Tokens)
        {
        }
    }
}
