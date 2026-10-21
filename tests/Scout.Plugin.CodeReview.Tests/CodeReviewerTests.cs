using Microsoft.Extensions.Options;
using Scout.Plugin.CodeReview.Abstractions;
using Scout.Plugin.CodeReview.Tests.Fakes;

namespace Scout.Plugin.CodeReview.Tests;

public sealed class CodeReviewerTests
{
    private static CodeReviewer Build(
        CapturingChatCompletionService chat, CodeReviewOptions? options = null)
        => new(() => chat, Options.Create(options ?? new CodeReviewOptions()));

    [Theory]
    [InlineData(ReviewKind.Quality)]
    [InlineData(ReviewKind.Vulnerabilities)]
    [InlineData(ReviewKind.Improvements)]
    [InlineData(ReviewKind.Tests)]
    [InlineData(ReviewKind.Explain)]
    [InlineData(ReviewKind.PullRequest)]
    [InlineData(ReviewKind.Repository)]
    public async Task Uses_System_Prompt_For_Kind(ReviewKind kind)
    {
        var chat = new CapturingChatCompletionService();
        var input = new CodeReviewInput(
            Snippet: "x", Diff: "diff", Files: new List<ReviewFile> { new("f.cs", "y") });

        await Build(chat).ReviewAsync(kind, input, CancellationToken.None);

        chat.SystemMessage.ShouldBe(CodeReviewPromptTemplate.SystemFor(kind));
    }

    [Fact]
    public async Task Wraps_User_Content_As_Untrusted()
    {
        var chat = new CapturingChatCompletionService();

        await Build(chat).ReviewAsync(
            ReviewKind.Quality, new CodeReviewInput(Snippet: "secret-code"), CancellationToken.None);

        var userMessage = chat.UserMessage.ShouldNotBeNull();
        userMessage.ShouldContain("UNTRUSTED_CONTENT");
        userMessage.ShouldContain("secret-code");
    }

    [Fact]
    public async Task Passes_Null_Kernel()
    {
        var chat = new CapturingChatCompletionService();

        await Build(chat).ReviewAsync(
            ReviewKind.Quality, new CodeReviewInput(Snippet: "code"), CancellationToken.None);

        chat.KernelWasNull.ShouldBeTrue();
    }

    [Fact]
    public async Task Does_Not_Enable_Function_Calling()
    {
        var chat = new CapturingChatCompletionService();

        await Build(chat).ReviewAsync(
            ReviewKind.Quality, new CodeReviewInput(Snippet: "code"), CancellationToken.None);

        chat.OpenAiSettings.ShouldNotBeNull();
        chat.OpenAiSettings!.FunctionChoiceBehavior.ShouldBeNull();
    }

    [Fact]
    public async Task Forwards_Temperature_And_MaxTokens_When_Set()
    {
        var chat = new CapturingChatCompletionService();
        var options = new CodeReviewOptions { Temperature = 0.2, MaxTokens = 512 };

        await Build(chat, options).ReviewAsync(
            ReviewKind.Quality, new CodeReviewInput(Snippet: "code"), CancellationToken.None);

        chat.OpenAiSettings!.Temperature.ShouldBe(0.2);
        chat.OpenAiSettings.MaxTokens.ShouldBe(512);
    }

    [Fact]
    public async Task Omits_Temperature_And_MaxTokens_When_Unset()
    {
        var chat = new CapturingChatCompletionService();

        await Build(chat).ReviewAsync(
            ReviewKind.Quality, new CodeReviewInput(Snippet: "code"), CancellationToken.None);

        chat.OpenAiSettings!.Temperature.ShouldBeNull();
        chat.OpenAiSettings.MaxTokens.ShouldBeNull();
    }

    [Fact]
    public async Task Repository_Review_Applies_Corpus_Budget_From_Options()
    {
        var chat = new CapturingChatCompletionService();
        var options = new CodeReviewOptions { CorpusCharBudget = 50 };
        // 'Z'/'Y' fill cannot appear in the hex Guid nonce, so counting them
        // measures only the surviving file bodies.
        var files = new List<ReviewFile>
        {
            new("a.cs", new string('Z', 500)),
            new("b.cs", new string('Y', 500)),
        };

        await Build(chat, options).ReviewAsync(
            ReviewKind.Repository, new CodeReviewInput(Files: files), CancellationToken.None);

        var userMessage = chat.UserMessage.ShouldNotBeNull();
        userMessage.Count(c => c == 'Z' || c == 'Y').ShouldBeLessThanOrEqualTo(50);
    }

    [Fact]
    public async Task Returns_Model_Content()
    {
        var chat = new CapturingChatCompletionService("the review");

        var result = await Build(chat).ReviewAsync(
            ReviewKind.Quality, new CodeReviewInput(Snippet: "code"), CancellationToken.None);

        result.ShouldBe("the review");
    }

    [Fact]
    public async Task Empty_Response_Throws_LlmReviewException()
    {
        var empty = new EmptyChatCompletionService();
        var reviewer = new CodeReviewer(() => empty, Options.Create(new CodeReviewOptions()));

        await Should.ThrowAsync<LlmReviewException>(() => reviewer.ReviewAsync(
            ReviewKind.Quality, new CodeReviewInput(Snippet: "code"), CancellationToken.None));
    }

    [Fact]
    public void Ctor_Throws_When_Chat_Factory_Null()
    {
        Should.Throw<ArgumentNullException>(
            () => new CodeReviewer(null!, Options.Create(new CodeReviewOptions())));
    }

    [Fact]
    public async Task Chat_Service_Resolved_Lazily_Not_At_Construction()
    {
        var resolved = false;

        // Constructing the reviewer must not pull the chat service; that is what
        // keeps the kernel from re-entering itself during graph construction.
        var reviewer = new CodeReviewer(
            () =>
            {
                resolved = true;
                return new CapturingChatCompletionService();
            },
            Options.Create(new CodeReviewOptions()));

        resolved.ShouldBeFalse();

        await reviewer.ReviewAsync(
            ReviewKind.Quality, new CodeReviewInput(Snippet: "code"), CancellationToken.None);

        resolved.ShouldBeTrue();
    }
}
