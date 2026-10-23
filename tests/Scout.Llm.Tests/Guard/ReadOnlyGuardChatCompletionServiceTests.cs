using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Scout.Llm.Configuration;
using Scout.Llm.Guard;

namespace Scout.Llm.Tests.Guard;

public sealed class ReadOnlyGuardChatCompletionServiceTests : IDisposable
{
    private const string RefusalText =
        "Scout is read-only: it can explain, summarise and draft, but it will not change code, " +
        "infrastructure, configuration, data or accounts. If you want a change, Scout can describe " +
        "the steps for you to apply.";

    private readonly IChatCompletionService _inner = Substitute.For<IChatCompletionService>();
    private readonly RecordingLogger _logger = new();
    private readonly List<HttpClient> _clients = [];

    public void Dispose() => _clients.ForEach(client => client.Dispose());

    private ReadOnlyGuardChatCompletionService Guard(HttpMessageHandler handler)
    {
        var http = new HttpClient(handler, disposeHandler: false);
        _clients.Add(http);
        return new(
            _inner,
            new TypeSafeClient(http, Options.Create(new TypeSafeGuardOptions { ApiKey = "ts-key" })),
            threshold: 0.5,
            _logger);
    }

    private static ChatHistory History(string userMessage)
    {
        var history = new ChatHistory();
        history.AddSystemMessage("You are Scout.");
        history.AddUserMessage(userMessage);
        return history;
    }

    private static async Task<List<StreamingChatMessageContent>> Collect(
        IAsyncEnumerable<StreamingChatMessageContent> stream)
    {
        var chunks = new List<StreamingChatMessageContent>();
        await foreach (var chunk in stream)
        {
            chunks.Add(chunk);
        }

        return chunks;
    }

    private static async IAsyncEnumerable<StreamingChatMessageContent> Stream(
        IEnumerable<StreamingChatMessageContent> chunks,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var chunk in chunks)
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            yield return chunk;
        }
    }

    private StreamingChatMessageContent[] InnerStreams(params string[] texts)
    {
        var chunks = texts.Select(t => new StreamingChatMessageContent(AuthorRole.Assistant, t)).ToArray();
        _inner.GetStreamingChatMessageContentsAsync(
                Arg.Any<ChatHistory>(), Arg.Any<PromptExecutionSettings?>(), Arg.Any<Kernel?>(), Arg.Any<CancellationToken>())
            .Returns(_ => Stream(chunks));
        return chunks;
    }

    [Fact]
    public async Task A_Request_Jev_Judges_As_A_Change_Is_Refused_And_The_Model_Is_Never_Called()
    {
        using var handler = StubTypeSafeHandler.Answering(0.9);
        InnerStreams("I deleted the branch.");

        var chunks = await Collect(Guard(handler).GetStreamingChatMessageContentsAsync(
            History("delete the release branch"), kernel: null));

        chunks.Count.ShouldBe(1);
        chunks[0].Role.ShouldBe(AuthorRole.Assistant);
        chunks[0].Content.ShouldBe(RefusalText);
        _inner.ReceivedCalls().ShouldBeEmpty();
        var info = _logger.Entries.ShouldHaveSingleItem();
        info.Level.ShouldBe(LogLevel.Information);
        info.Message.ShouldBe("Read-only guard refused a request (p=0.90)");
    }

    [Fact]
    public async Task A_Non_Streaming_Request_Jev_Judges_As_A_Change_Is_Refused_And_The_Model_Is_Never_Called()
    {
        using var handler = StubTypeSafeHandler.Answering(0.9);

        var messages = await Guard(handler).GetChatMessageContentsAsync(History("merge PR 42"));

        messages.ShouldHaveSingleItem().Content.ShouldBe(RefusalText);
        _inner.ReceivedCalls().ShouldBeEmpty();
    }

    [Fact]
    public async Task An_Ordinary_Question_Is_Answered_By_The_Model_With_Its_Chunks_Unchanged()
    {
        using var handler = StubTypeSafeHandler.Answering(0.1);
        var expected = InnerStreams("The build ", "is green.");
        var history = History("is the build green?");
        var settings = new PromptExecutionSettings();
        var kernel = new Kernel();

        var chunks = await Collect(Guard(handler).GetStreamingChatMessageContentsAsync(
            history, settings, kernel, CancellationToken.None));

        chunks.ShouldBe(expected);
        _ = _inner.Received(1).GetStreamingChatMessageContentsAsync(
            history, settings, kernel, Arg.Any<CancellationToken>());
        _logger.Entries.ShouldBeEmpty();
    }

    [Fact]
    public async Task When_TypeSafe_Fails_The_Model_Still_Answers_And_A_Warning_Is_Logged()
    {
        using var handler = StubTypeSafeHandler.Failing(HttpStatusCode.InternalServerError, "boom");
        var expected = InnerStreams("answer");

        var chunks = await Collect(Guard(handler).GetStreamingChatMessageContentsAsync(
            History("is the build green?"), kernel: null));

        chunks.ShouldBe(expected);
        var warning = _logger.Entries.ShouldHaveSingleItem();
        warning.Level.ShouldBe(LogLevel.Warning);
        warning.Message.ShouldContain("status=500");
        warning.Message.ShouldNotContain("ts-key");
    }

    [Fact]
    public async Task The_State_Sent_To_Jev_Is_The_Twelve_Turns_Before_The_Latest_User_Message_Tail_Truncated()
    {
        using var handler = StubTypeSafeHandler.Answering(0.1);
        InnerStreams("ok");
        var history = new ChatHistory();
        history.AddSystemMessage("You are Scout. system-text-never-sent");
        for (var turn = 1; turn <= 14; turn++)
        {
            var text = turn == 13 ? new string('h', 500) + new string('t', 1000) : $"turn {turn}";
            if (turn % 2 == 1)
            {
                history.AddUserMessage(text);
            }
            else
            {
                history.AddAssistantMessage(text);
            }
        }

        history.AddUserMessage(new string('o', 500) + new string('l', 4000));

        await Collect(Guard(handler).GetStreamingChatMessageContentsAsync(history, kernel: null));

        using var body = JsonDocument.Parse(handler.Body!);
        var state = body.RootElement.GetProperty("state");
        state.GetProperty("latest_user_message").GetString().ShouldBe(new string('l', 4000));
        var turns = state.GetProperty("recent_turns").EnumerateArray()
            .Select(t => (t.GetProperty("role").GetString(), t.GetProperty("text").GetString()))
            .ToList();
        turns.ShouldBe(
        [
            ("user", "turn 3"), ("assistant", "turn 4"), ("user", "turn 5"), ("assistant", "turn 6"),
            ("user", "turn 7"), ("assistant", "turn 8"), ("user", "turn 9"), ("assistant", "turn 10"),
            ("user", "turn 11"), ("assistant", "turn 12"), ("user", new string('t', 1000)), ("assistant", "turn 14"),
        ]);
        handler.Body!.ShouldNotContain("system-text-never-sent");
    }

    [Fact]
    public async Task A_Confirmation_Offer_At_The_End_Of_A_Long_Reply_Reaches_Jev()
    {
        const string Offer = "Shall I restart the prod server now?";
        using var handler = new StubTypeSafeHandler(async (request, ct) =>
        {
            var posted = await request.Content!.ReadAsStringAsync(ct);
            return StubTypeSafeHandler.Noul(posted.Contains(Offer, StringComparison.Ordinal) ? 0.9 : 0.1);
        });
        InnerStreams("Restarting now.");
        var history = new ChatHistory();
        history.AddUserMessage("why is prod slow?");
        history.AddAssistantMessage(new string('x', 1500 - Offer.Length - 1) + " " + Offer);
        history.AddUserMessage("yes");

        var chunks = await Collect(Guard(handler).GetStreamingChatMessageContentsAsync(history, kernel: null));

        chunks.ShouldHaveSingleItem().Content.ShouldBe(RefusalText);
        _inner.ReceivedCalls().ShouldBeEmpty();
    }

    [Fact]
    public async Task System_And_Tool_Messages_Are_Never_Sent_To_Jev()
    {
        using var handler = StubTypeSafeHandler.Answering(0.1);
        InnerStreams("ok");
        var history = new ChatHistory();
        history.AddSystemMessage("system-text-never-sent");
        history.AddUserMessage("u1");
        history.Add(new ChatMessageContent(AuthorRole.Tool, "tool-output-never-sent"));
        history.AddUserMessage("the latest message");

        await Collect(Guard(handler).GetStreamingChatMessageContentsAsync(history, kernel: null));

        var posted = handler.Body!;
        posted.ShouldContain("u1");
        posted.ShouldNotContain("system-text-never-sent");
        posted.ShouldNotContain("tool-output-never-sent");
    }

    [Fact]
    public async Task A_Short_Confirmation_Of_A_Proposed_Change_Is_Refused_Because_Jev_Sees_The_Proposal()
    {
        using var handler = new StubTypeSafeHandler(async (request, ct) =>
        {
            var posted = await request.Content!.ReadAsStringAsync(ct);
            var noul = posted.Contains("plan the restart of the prod server", StringComparison.Ordinal) ? 0.9 : 0.1;
            return StubTypeSafeHandler.Noul(noul);

        });
        InnerStreams("Restarting now.");
        var history = new ChatHistory();
        history.AddUserMessage("plan the restart of the prod server");
        history.AddAssistantMessage("Drain traffic, restart the service, check health.");
        history.AddUserMessage("ok, do it");

        var chunks = await Collect(Guard(handler).GetStreamingChatMessageContentsAsync(history, kernel: null));

        chunks.ShouldHaveSingleItem().Content.ShouldBe(RefusalText);
        _inner.ReceivedCalls().ShouldBeEmpty();
    }

    [Fact]
    public async Task Jev_Is_Asked_Whether_The_Message_Asks_For_A_Change_Outside_The_Conversation()
    {
        using var handler = StubTypeSafeHandler.Answering(0.1);
        InnerStreams("ok");

        await Collect(Guard(handler).GetStreamingChatMessageContentsAsync(History("hi"), kernel: null));

        using var body = JsonDocument.Parse(handler.Body!);
        var question = body.RootElement.GetProperty("questions").GetProperty("executive_write");
        question.GetProperty("instructions").GetString().ShouldBe(
            "Judge `latest_user_message` in the context of `recent_turns`: does it ask the assistant to make a " +
            "change outside this conversation, such as creating, editing, deleting, deploying, merging, approving, " +
            "sending, posting elsewhere, restarting, scaling, or changing permissions in code, infrastructure, " +
            "configuration, data, tickets or accounts? A short confirmation like 'ok, do it' counts as a change " +
            "request when the turns before it proposed one.");
        question.GetProperty("criteria").GetProperty("true").GetString()
            .ShouldBe("The user wants the assistant to perform such a change.");
        question.GetProperty("criteria").GetProperty("false").GetString().ShouldBe(
            "The user asks a question, wants an explanation, a summary, a plan, a review, or a draft that a " +
            "person will apply themselves; or the message is small talk.");
    }

    [Fact]
    public async Task A_Probability_Exactly_At_The_Threshold_Is_Refused()
    {
        using var handler = StubTypeSafeHandler.Answering(0.5);

        var messages = await Guard(handler).GetChatMessageContentsAsync(History("scale the cluster to 0"));

        messages.ShouldHaveSingleItem().Content.ShouldBe(RefusalText);
        _inner.ReceivedCalls().ShouldBeEmpty();
    }

    private sealed class RecordingLogger : ILogger<ReadOnlyGuardChatCompletionService>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));
    }
}
