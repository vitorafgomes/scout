using System.Net.Http;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Scout.Plugin.CodeQA.Composing;
using Scout.Plugin.CodeQA.Domain;
using Scout.Plugin.CodeQA.Storage;

namespace Scout.Plugin.CodeQA.Tests.Composing;

public class LlmComposerTests
{
    private static VectorSearchResult MakeHit(
        string path = "src/A.cs",
        int start = 1,
        int end = 5,
        string content = "class A {}",
        double score = 0.8)
    {
        var embedding = new ReadOnlyMemory<float>(new[] { 0.1f, 0.2f });
        var chunk = new CodeChunk("id-" + path + "-" + start, path, start, end, content, embedding);
        return new VectorSearchResult(chunk, score);
    }

    private static IOptions<CodeQAOptions> DefaultOptions(
        Action<CodeQAOptions>? configure = null)
    {
        var opts = new CodeQAOptions();
        opts.Retrieval.MaxContextTokens = 4000;
        opts.Chunking.ApproxBytesPerToken = 4;
        opts.Composer.Temperature = 0.2;
        opts.Composer.MaxOutputTokens = 512;
        opts.Composer.Model = string.Empty;
        configure?.Invoke(opts);
        return Options.Create(opts);
    }

    private static IChatCompletionService ChatReturning(string content, string? modelId = "test-model")
    {
        var chat = Substitute.For<IChatCompletionService>();
        var msg = new ChatMessageContent(AuthorRole.Assistant, content) { ModelId = modelId };
        chat
            .GetChatMessageContentsAsync(
                Arg.Any<ChatHistory>(),
                Arg.Any<PromptExecutionSettings?>(),
                Arg.Any<Kernel?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<ChatMessageContent>>(new[] { msg }));
        return chat;
    }

    [Fact]
    public void Ctor_Throws_On_Null_Options()
    {
        Should.Throw<ArgumentNullException>(() =>
            new LlmComposer(null!, Substitute.For<IChatCompletionService>()));
    }

    [Fact]
    public void Ctor_Throws_On_Null_Chat()
    {
        Should.Throw<ArgumentNullException>(() =>
            new LlmComposer(DefaultOptions(), null!));
    }

    [Fact]
    public async Task Compose_Throws_On_Empty_Question()
    {
        var composer = new LlmComposer(DefaultOptions(), ChatReturning("ok"));

        await Should.ThrowAsync<ArgumentException>(() =>
            composer.ComposeAsync("   ", new[] { MakeHit() }, CancellationToken.None));
    }

    [Fact]
    public async Task Compose_Throws_On_Null_Context()
    {
        var composer = new LlmComposer(DefaultOptions(), ChatReturning("ok"));

        await Should.ThrowAsync<ArgumentNullException>(() =>
            composer.ComposeAsync("hi", null!, CancellationToken.None));
    }

    [Fact]
    public async Task Compose_Returns_CodeAnswer_With_Text_From_Chat()
    {
        var composer = new LlmComposer(DefaultOptions(), ChatReturning("Hello from model."));

        var answer = await composer.ComposeAsync(
            "What?", new[] { MakeHit() }, CancellationToken.None);

        answer.Text.ShouldBe("Hello from model.");
    }

    [Fact]
    public async Task Compose_Builds_One_Citation_Per_Context_Chunk()
    {
        var hits = new[]
        {
            MakeHit("a.cs", 1, 5, "A", 0.9),
            MakeHit("b.cs", 10, 20, "B", 0.6),
        };
        var composer = new LlmComposer(DefaultOptions(), ChatReturning("answer"));

        var answer = await composer.ComposeAsync("q", hits, CancellationToken.None);

        answer.Citations.Count.ShouldBe(2);
        answer.Citations[0].FilePath.ShouldBe("a.cs");
        answer.Citations[0].StartLine.ShouldBe(1);
        answer.Citations[0].EndLine.ShouldBe(5);
        answer.Citations[0].RelevanceScore.ShouldBe(0.9);
        answer.Citations[1].FilePath.ShouldBe("b.cs");
    }

    [Fact]
    public async Task Compose_Clamps_Score_To_Minus_One_To_One()
    {
        var hit = MakeHit(score: 5.0); // out of [-1, 1]
        var composer = new LlmComposer(DefaultOptions(), ChatReturning("answer"));

        var answer = await composer.ComposeAsync("q", new[] { hit }, CancellationToken.None);

        answer.Citations[0].RelevanceScore.ShouldBe(1.0);
    }

    [Fact]
    public async Task Compose_Truncates_Context_By_Token_Budget()
    {
        // ApproxBytesPerToken=4, MaxContextTokens=10 => ~40 bytes / chunk cap.
        // Each chunk below contributes (Content.Length/4) + 20 overhead tokens.
        var opts = DefaultOptions(o =>
        {
            o.Retrieval.MaxContextTokens = 30;
            o.Chunking.ApproxBytesPerToken = 4;
        });
        var hits = new[]
        {
            MakeHit("a.cs", 1, 5, new string('x', 40)),
            MakeHit("b.cs", 1, 5, new string('y', 40)),
            MakeHit("c.cs", 1, 5, new string('z', 40)),
        };
        var composer = new LlmComposer(opts, ChatReturning("ok"));

        var answer = await composer.ComposeAsync("q", hits, CancellationToken.None);

        // Budget too tight for two chunks; the first is always kept.
        answer.Citations.Count.ShouldBe(1);
        answer.Citations[0].FilePath.ShouldBe("a.cs");
    }

    [Fact]
    public async Task Compose_Keeps_At_Least_One_Chunk_Even_When_Over_Budget()
    {
        var opts = DefaultOptions(o =>
        {
            o.Retrieval.MaxContextTokens = 1; // absurdly small
        });
        var composer = new LlmComposer(opts, ChatReturning("ok"));

        var answer = await composer.ComposeAsync(
            "q",
            new[] { MakeHit("a.cs", 1, 5, new string('x', 1000)) },
            CancellationToken.None);

        answer.Citations.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Compose_Uses_Configured_Model_When_Set()
    {
        var opts = DefaultOptions(o => o.Composer.Model = "my-model");
        var composer = new LlmComposer(opts, ChatReturning("ok", modelId: "ignored"));

        var answer = await composer.ComposeAsync("q", new[] { MakeHit() }, CancellationToken.None);

        answer.ModelUsed.ShouldBe("my-model");
    }

    [Fact]
    public async Task Compose_Falls_Back_To_Chat_ModelId_When_Not_Configured()
    {
        var composer = new LlmComposer(DefaultOptions(), ChatReturning("ok", modelId: "provider-x"));

        var answer = await composer.ComposeAsync("q", new[] { MakeHit() }, CancellationToken.None);

        answer.ModelUsed.ShouldBe("provider-x");
    }

    [Fact]
    public async Task Compose_Defaults_Model_To_Placeholder_When_Nothing_Reported()
    {
        var composer = new LlmComposer(DefaultOptions(), ChatReturning("ok", modelId: null));

        var answer = await composer.ComposeAsync("q", new[] { MakeHit() }, CancellationToken.None);

        answer.ModelUsed.ShouldBe("(provider default)");
    }

    [Fact]
    public async Task Compose_Reports_Latency_From_TimeProvider()
    {
        var chat = Substitute.For<IChatCompletionService>();
        var time = new TestTimeProvider();
        chat
            .GetChatMessageContentsAsync(
                Arg.Any<ChatHistory>(),
                Arg.Any<PromptExecutionSettings?>(),
                Arg.Any<Kernel?>(),
                Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                // Simulate time passing while the chat call runs.
                time.Advance(TimeSpan.FromMilliseconds(250));
                var msg = new ChatMessageContent(AuthorRole.Assistant, "ok") { ModelId = "m" };
                return Task.FromResult<IReadOnlyList<ChatMessageContent>>(new[] { msg });
            });
        var composer = new LlmComposer(DefaultOptions(), chat, time);

        var answer = await composer.ComposeAsync("q", new[] { MakeHit() }, CancellationToken.None);

        answer.Latency.ShouldBe(TimeSpan.FromMilliseconds(250));
    }

    [Fact]
    public async Task Compose_Clamps_Negative_Latency_To_Zero()
    {
        var chat = Substitute.For<IChatCompletionService>();
        var time = new TestTimeProvider();
        time.SetTicks(100_000);
        chat
            .GetChatMessageContentsAsync(
                Arg.Any<ChatHistory>(),
                Arg.Any<PromptExecutionSettings?>(),
                Arg.Any<Kernel?>(),
                Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                // Clock skew simulation: counter runs backwards.
                time.SetTicks(50_000);
                var msg = new ChatMessageContent(AuthorRole.Assistant, "ok") { ModelId = "m" };
                return Task.FromResult<IReadOnlyList<ChatMessageContent>>(new[] { msg });
            });
        var composer = new LlmComposer(DefaultOptions(), chat, time);

        var answer = await composer.ComposeAsync("q", new[] { MakeHit() }, CancellationToken.None);

        answer.Latency.ShouldBe(TimeSpan.Zero);
    }

    [Fact]
    public async Task Compose_Empty_Response_Throws_InvalidOperationException()
    {
        var composer = new LlmComposer(DefaultOptions(), ChatReturning("   "));

        var ex = await Should.ThrowAsync<InvalidOperationException>(() =>
            composer.ComposeAsync("q", new[] { MakeHit() }, CancellationToken.None));

        ex.Message.ShouldContain("empty response");
    }

    [Fact]
    public async Task Compose_Propagates_HttpRequestException()
    {
        var chat = Substitute.For<IChatCompletionService>();
        chat
            .GetChatMessageContentsAsync(
                Arg.Any<ChatHistory>(),
                Arg.Any<PromptExecutionSettings?>(),
                Arg.Any<Kernel?>(),
                Arg.Any<CancellationToken>())
            .Returns<Task<IReadOnlyList<ChatMessageContent>>>(_ =>
                throw new HttpRequestException("connection refused"));
        var composer = new LlmComposer(DefaultOptions(), chat);

        await Should.ThrowAsync<HttpRequestException>(() =>
            composer.ComposeAsync("q", new[] { MakeHit() }, CancellationToken.None));
    }

    [Fact]
    public async Task Compose_Propagates_TaskCanceledException_From_Timeout()
    {
        var chat = Substitute.For<IChatCompletionService>();
        chat
            .GetChatMessageContentsAsync(
                Arg.Any<ChatHistory>(),
                Arg.Any<PromptExecutionSettings?>(),
                Arg.Any<Kernel?>(),
                Arg.Any<CancellationToken>())
            .Returns<Task<IReadOnlyList<ChatMessageContent>>>(_ =>
                throw new TaskCanceledException("timeout"));
        var composer = new LlmComposer(DefaultOptions(), chat);

        await Should.ThrowAsync<TaskCanceledException>(() =>
            composer.ComposeAsync("q", new[] { MakeHit() }, CancellationToken.None));
    }

    [Fact]
    public async Task Compose_Propagates_OperationCanceledException_From_Caller()
    {
        var chat = Substitute.For<IChatCompletionService>();
        chat
            .GetChatMessageContentsAsync(
                Arg.Any<ChatHistory>(),
                Arg.Any<PromptExecutionSettings?>(),
                Arg.Any<Kernel?>(),
                Arg.Any<CancellationToken>())
            .Returns<Task<IReadOnlyList<ChatMessageContent>>>(ci =>
            {
                var ct = ci.Arg<CancellationToken>();
                ct.ThrowIfCancellationRequested();
                throw new InvalidOperationException("should have thrown OCE");
            });
        var composer = new LlmComposer(DefaultOptions(), chat);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(() =>
            composer.ComposeAsync("q", new[] { MakeHit() }, cts.Token));
    }

    [Fact]
    public async Task Compose_Passes_Temperature_And_MaxTokens_To_Chat()
    {
        PromptExecutionSettings? captured = null;
        var chat = Substitute.For<IChatCompletionService>();
        chat
            .GetChatMessageContentsAsync(
                Arg.Any<ChatHistory>(),
                Arg.Any<PromptExecutionSettings?>(),
                Arg.Any<Kernel?>(),
                Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                captured = ci.ArgAt<PromptExecutionSettings?>(1);
                var msg = new ChatMessageContent(AuthorRole.Assistant, "ok") { ModelId = "m" };
                return Task.FromResult<IReadOnlyList<ChatMessageContent>>(new[] { msg });
            });
        var opts = DefaultOptions(o =>
        {
            o.Composer.Temperature = 0.7;
            o.Composer.MaxOutputTokens = 123;
        });
        var composer = new LlmComposer(opts, chat);

        await composer.ComposeAsync("q", new[] { MakeHit() }, CancellationToken.None);

        captured.ShouldNotBeNull();
        captured!.ExtensionData.ShouldNotBeNull();
        captured.ExtensionData!["temperature"].ShouldBe(0.7);
        captured.ExtensionData["max_tokens"].ShouldBe(123);
    }

    [Fact]
    public async Task Compose_Sends_System_And_User_Messages_In_Order()
    {
        ChatHistory? captured = null;
        var chat = Substitute.For<IChatCompletionService>();
        chat
            .GetChatMessageContentsAsync(
                Arg.Any<ChatHistory>(),
                Arg.Any<PromptExecutionSettings?>(),
                Arg.Any<Kernel?>(),
                Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                captured = ci.ArgAt<ChatHistory>(0);
                var msg = new ChatMessageContent(AuthorRole.Assistant, "ok") { ModelId = "m" };
                return Task.FromResult<IReadOnlyList<ChatMessageContent>>(new[] { msg });
            });
        var composer = new LlmComposer(DefaultOptions(), chat);

        await composer.ComposeAsync("Explain A", new[] { MakeHit("a.cs", 1, 2, "class A {}") }, CancellationToken.None);

        captured.ShouldNotBeNull();
        captured!.Count.ShouldBe(2);
        captured[0].Role.ShouldBe(AuthorRole.System);
        captured[1].Role.ShouldBe(AuthorRole.User);
        var userContent = captured[1].Content;
        userContent.ShouldNotBeNull();
        userContent!.ShouldContain("QUESTION: Explain A");
        userContent.ShouldContain("class A {}");
    }
}
