using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Scout.Conversation.Abstractions;
using Scout.Conversation.Streaming;
using Scout.Conversation.Tests.Fakes;
using Scout.Llm.Abstractions;

namespace Scout.Conversation.Tests.Streaming;

/// <summary>
/// Boundary failure injection at the stream and sink edges. The pump is
/// transport-agnostic: it does not retry; a non-cancellation interim throw is
/// swallowed (the transport owns its own resilience), the buffer is retained,
/// and finalize stays coherent. Hermetic — no real transport.
/// </summary>
public sealed class StreamPumpFailureTests
{
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(750);
    private const string Cursor = "▌";
    private const int Limit = 300;

    private const string NoAnswerSentence =
        "The answer stopped before any text arrived because the conversation and what Scout read for it filled the model's context window.";
    private const string ModelNotFoundSentence =
        "The AI Source does not have the model qwen2.5:3b. A local model is not installed yet or is still downloading: check AI Sources in Scout Desktop.";
    private const string TruncationMarker = "... (truncated)\n\n";
    private const string CutMarker = "... (truncated)";
    private const string LateFailure = "the abandoned write failed after the run had ended";
    private static readonly TimeSpan Ceiling = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan Grace = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan OneSecond = TimeSpan.FromSeconds(1);
    private static readonly StreamPumpOptions Defaults = new();

    private static readonly RenderCapabilities FinalOnly = new()
    {
        MaxBodyLength = Limit,
    };

    private static readonly RenderCapabilities FineGrained = new()
    {
        FineGrainedEdit = true,
        MaxBodyLength = 4096,
    };

    private static FakeTimeProvider Clock() =>
        new(new DateTimeOffset(2026, 5, 28, 0, 0, 0, TimeSpan.Zero));

    private static StreamPump Pump(FakeTimeProvider time, ILogger? logger = null)
        => new(new StreamPumpOptions
        {
            EditInterval = Interval,
            Cursor = Cursor,
            TimeProvider = time,
            Logger = logger,
        });

    private static async IAsyncEnumerable<string> Ticking(
        FakeTimeProvider time,
        [EnumeratorCancellation] CancellationToken cancellationToken,
        params string[] chunks)
    {
        foreach (var chunk in chunks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            time.Advance(Interval);
            await Task.Yield();
            yield return chunk;
        }
    }

    [Fact]
    public async Task Transient_Interim_Update_Throw_Is_Swallowed_Finalize_Coherent()
    {
        var time = Clock();
        // Any non-cancellation interim failure is dropped by the pump; the
        // buffer is retained and the final body is still coherent. The pump
        // itself does not retry — that is the transport's (sink's) job.
        var sink = new RecordingResponseSink(FineGrained, onUpdate: (index, _) =>
        {
            if (index == 1)
            {
                throw new InvalidOperationException("transient transport failure");
            }
        });

        var response = new ChatResponse(Ticking(time, default, "a", "b", "c"));

        await Should.NotThrowAsync(
            () => Pump(time).RunAsync(response, sink, CancellationToken.None));

        sink.Completed.ShouldBeTrue();
        sink.Failed.ShouldBeFalse();
        sink.TerminalCount.ShouldBe(1);
        sink.FinalText.ShouldBe("abc");
    }

    [Fact]
    public async Task Stream_Throws_MidStream_Fails_With_Partial_Buffer_No_Complete()
    {
        var time = Clock();
        var sink = new RecordingResponseSink(FineGrained);
        var response = new ChatResponse(
            AsyncSequence.ThenThrow(
                new InvalidOperationException("connection reset"),
                default,
                "par", "tial"));

        await Should.NotThrowAsync(
            () => Pump(time).RunAsync(response, sink, CancellationToken.None));

        sink.Failed.ShouldBeTrue();
        sink.Completed.ShouldBeFalse();
        sink.TerminalCount.ShouldBe(1);
        sink.FinalText.ShouldBe("partial");
        sink.FailReason.ShouldNotBeNull();
        sink.FailReason!.ShouldContain("connection reset");
    }

    [Fact]
    public async Task Cancellation_MidStream_Propagates_No_Forced_Terminal_Edit()
    {
        var time = Clock();
        var sink = new RecordingResponseSink(FineGrained);
        using var cts = new CancellationTokenSource();
        var response = new ChatResponse(
            AsyncSequence.CancelAfterFirst(cts, default, "first", "second", "third"));

        await Should.ThrowAsync<OperationCanceledException>(
            () => Pump(time).RunAsync(response, sink, cts.Token));

        // No terminal edit was forced; sink is not corrupted (Begin happened,
        // no Complete/Fail).
        sink.BeginCount.ShouldBe(1);
        sink.Completed.ShouldBeFalse();
        sink.Failed.ShouldBeFalse();
        sink.TerminalCount.ShouldBe(0);
    }

    [Fact]
    public async Task RateLimit_MidStream_Finalizes_With_Friendly_Message_And_Quiet_Log()
    {
        var time = Clock();
        var logger = new RecordingLogger();
        var sink = new RecordingResponseSink(FineGrained);
        var response = new ChatResponse(
            AsyncSequence.ThenThrow(
                new LlmRateLimitException("LLM rate limit (HTTP 429)."),
                default,
                "par", "tial"));

        await Should.NotThrowAsync(
            () => Pump(time, logger).RunAsync(response, sink, CancellationToken.None));

        // Finalized via Complete (not Fail) so the user sees the friendly text.
        sink.Completed.ShouldBeTrue();
        sink.Failed.ShouldBeFalse();
        sink.TerminalCount.ShouldBe(1);
        sink.FinalText.ShouldNotBeNull();
        sink.FinalText!.ShouldContain("rate limit");
        sink.FinalText!.ShouldContain("partial");

        // Quiet log: nothing logged with an exception and no Warning-level noise.
        logger.Entries.Any(e => e.Exception != null).ShouldBeFalse();
        logger.EntriesAt(LogLevel.Warning).ShouldBeEmpty();
    }

    [Fact]
    public async Task When_The_Claude_Api_Account_Has_No_Credit_Left_The_Reply_Says_The_Balance_Ran_Out_Not_An_Error()
    {
        var time = Clock();
        var logger = new RecordingLogger();
        var sink = new RecordingResponseSink(FineGrained);
        var response = new ChatResponse(
            AsyncSequence.ThenThrow(new LlmQuotaExceededException("credit_balance_too_low"), default));

        await Should.NotThrowAsync(
            () => Pump(time, logger).RunAsync(response, sink, CancellationToken.None));

        sink.Completed.ShouldBeTrue();
        sink.Failed.ShouldBeFalse();
        sink.TerminalCount.ShouldBe(1);
        sink.FinalText.ShouldBe(
            "The AI provider's account has run out of credit. Add credit with the provider, or choose another AI Source in Scout Desktop.");
        sink.FinalText!.ShouldNotContain("error");
        sink.FinalText!.ShouldNotContain("rejected");
        sink.FinalText!.ShouldNotContain("failed");

        var warning = logger.EntriesAt(LogLevel.Warning).ShouldHaveSingleItem();
        warning.Message.ShouldContain("refused the request for quota or billing (code credit_balance_too_low)", Case.Sensitive);
        warning.Exception.ShouldBeNull();
    }

    [Fact]
    public async Task When_The_Provider_Does_Not_Have_The_Model_The_Reply_Names_It_And_Says_Where_To_Look_Not_An_Error()
    {
        var time = Clock();
        var logger = new RecordingLogger();
        var sink = new RecordingResponseSink(FineGrained);
        var response = new ChatResponse(
            AsyncSequence.ThenThrow(new LlmModelNotFoundException("qwen2.5:3b"), default));

        await Should.NotThrowAsync(
            () => Pump(time, logger).RunAsync(response, sink, CancellationToken.None));

        sink.Completed.ShouldBeTrue();
        sink.Failed.ShouldBeFalse();
        sink.TerminalCount.ShouldBe(1);
        sink.FinalText.ShouldBe(ModelNotFoundSentence);
        sink.FinalText!.ShouldNotContain("error", Case.Insensitive);
        sink.FinalText!.ShouldNotContain("failed", Case.Insensitive);

        var warning = logger.EntriesAt(LogLevel.Warning).ShouldHaveSingleItem();
        warning.Message.ShouldContain("qwen2.5:3b", Case.Sensitive);
        warning.Exception.ShouldBeNull();
    }

    [Fact]
    public async Task Quota_Exceeded_MidStream_Tells_The_User_The_Account_Has_No_Credit()
    {
        var time = Clock();
        var logger = new RecordingLogger();
        var sink = new RecordingResponseSink(FineGrained);
        var response = new ChatResponse(
            AsyncSequence.ThenThrow(
                new LlmQuotaExceededException("credit_balance_exhausted"),
                default,
                "par", "tial"));

        await Should.NotThrowAsync(
            () => Pump(time, logger).RunAsync(response, sink, CancellationToken.None));

        sink.Completed.ShouldBeTrue();
        sink.Failed.ShouldBeFalse();
        sink.TerminalCount.ShouldBe(1);
        sink.FinalText.ShouldBe("partial\n\n" + new StreamPumpOptions().QuotaExceededMessage);
        sink.FinalText!.ShouldContain("no credit left");
        sink.FinalText!.ShouldNotContain("try again");
        sink.FinalText!.ShouldNotContain("rate limit");
        sink.FinalText!.ShouldNotContain("connection error");

        var warning = logger.EntriesAt(LogLevel.Warning).ShouldHaveSingleItem();
        warning.Message.ShouldContain("credit_balance_exhausted");
        warning.Exception.ShouldBeNull();
    }

    [Fact]
    public async Task Sign_In_Required_MidStream_Tells_The_User_To_Sign_In_Again()
    {
        var time = Clock();
        var logger = new RecordingLogger();
        var sink = new RecordingResponseSink(FineGrained);
        var response = new ChatResponse(
            AsyncSequence.ThenThrow(
                new LlmSignInRequiredException(),
                default,
                "par", "tial"));

        await Should.NotThrowAsync(
            () => Pump(time, logger).RunAsync(response, sink, CancellationToken.None));

        sink.Completed.ShouldBeTrue();
        sink.Failed.ShouldBeFalse();
        sink.TerminalCount.ShouldBe(1);
        sink.FinalText.ShouldBe(
            "partial\n\nChatGPT sign-in is missing or expired. Sign in again on the AI Sources screen in Scout.");
        sink.FinalText!.ShouldNotContain("connection error");

        var warning = logger.EntriesAt(LogLevel.Warning).ShouldHaveSingleItem();
        warning.Exception.ShouldBeNull();
    }

    [Fact]
    public async Task Key_Rejected_MidStream_Tells_The_User_To_Check_The_Key_In_Scout_Desktop()
    {
        var time = Clock();
        var logger = new RecordingLogger();
        var sink = new RecordingResponseSink(FineGrained);
        var response = new ChatResponse(
            AsyncSequence.ThenThrow(
                new LlmKeyRejectedException(),
                default,
                "par", "tial"));

        await Should.NotThrowAsync(
            () => Pump(time, logger).RunAsync(response, sink, CancellationToken.None));

        sink.Completed.ShouldBeTrue();
        sink.Failed.ShouldBeFalse();
        sink.TerminalCount.ShouldBe(1);
        sink.FinalText.ShouldBe("partial\n\nThe AI Source rejected its key; check it in Scout Desktop.");
        var warning = logger.EntriesAt(LogLevel.Warning).ShouldHaveSingleItem();
        warning.Exception.ShouldBeNull();
    }

    [Fact]
    public async Task Generic_Failure_MidStream_Still_Warns_With_Stack_And_Fails()
    {
        var time = Clock();
        var logger = new RecordingLogger();
        var sink = new RecordingResponseSink(FineGrained);
        var response = new ChatResponse(
            AsyncSequence.ThenThrow(
                new InvalidOperationException("connection reset"),
                default,
                "par", "tial"));

        await Should.NotThrowAsync(
            () => Pump(time, logger).RunAsync(response, sink, CancellationToken.None));

        sink.Failed.ShouldBeTrue();
        sink.FailReason.ShouldNotBeNull();
        sink.FailReason!.ShouldContain("connection reset");

        // Generic failures keep the stack: a Warning entry carrying the exception.
        logger.EntriesAt(LogLevel.Warning).Any(e => e.Exception != null).ShouldBeTrue();
    }

    public static TheoryData<string, string> EveryEndingOverEveryPartialText()
    {
        var rows = new TheoryData<string, string>();
        foreach (var ending in new[] { "rate limit", "quota", "credit ran out", "plan usage limit", "sign-in", "key", "model not found", "vision", "no answer", "failure" })
        {
            foreach (var partial in new[] { "empty", "short", "at the limit", "over the limit" })
            {
                rows.Add(ending, partial);
            }
        }

        return rows;
    }

    [Theory]
    [MemberData(nameof(EveryEndingOverEveryPartialText))]
    public async Task Every_Error_Ending_Is_Delivered_Within_The_Sinks_Length_Limit_With_Its_Whole_Sentence(
        string ending, string partial)
    {
        var sink = new RecordingResponseSink(FinalOnly);
        var response = new ChatResponse(AsyncSequence.ThenThrow(ErrorOf(ending), default, PartialOf(partial)));

        await Pump(Clock()).RunAsync(response, sink, CancellationToken.None);

        sink.TerminalCount.ShouldBe(1);
        sink.Failed.ShouldBe(ending == "failure");
        sink.FinalText.ShouldNotBeNull();
        sink.FinalText.Length.ShouldBeLessThanOrEqualTo(
            Limit, $"the {ending} ending over a partial text {partial} must fit the sink's limit");
        sink.FinalText.ShouldEndWith(MustEndWith(ending, partial), Case.Sensitive);
        if (WholeBodyOf(ending, partial) is { } whole)
        {
            sink.FinalText.ShouldBe(whole, $"a {partial} partial text needs no cut before the {ending} ending");
        }
    }

    [Fact]
    public async Task The_Partial_Text_Gives_Way_To_The_Sentence_And_Keeps_Its_Newest_Part()
    {
        var sink = new RecordingResponseSink(FinalOnly);
        var text = Letters(Limit * 3);
        var response = new ChatResponse(AsyncSequence.ThenThrow(ErrorOf("rate limit"), default, text));

        await Pump(Clock()).RunAsync(response, sink, CancellationToken.None);

        sink.FinalText.ShouldNotBeNull();
        sink.FinalText.Length.ShouldBe(Limit);
        sink.FinalText.ShouldStartWith(TruncationMarker, Case.Sensitive);
        sink.FinalText.ShouldEndWith(text[^20..] + "\n\n" + Defaults.RateLimitMessage, Case.Sensitive);
    }

    [Fact]
    public async Task A_Failed_Stream_Hands_The_Sink_The_First_Part_Of_The_Partial_Text_With_One_Marker_At_Its_End()
    {
        var sink = new RecordingResponseSink(FinalOnly);
        var text = Letters(Limit * 3);
        var response = new ChatResponse(AsyncSequence.ThenThrow(ErrorOf("failure"), default, text));

        await Pump(Clock()).RunAsync(response, sink, CancellationToken.None);

        sink.Failed.ShouldBeTrue();
        sink.FinalText.ShouldBe(text[..(Limit - CutMarker.Length)] + CutMarker);
    }

    [Theory]
    [InlineData("rate limit")]
    [InlineData("quota")]
    [InlineData("plan usage limit")]
    [InlineData("sign-in")]
    [InlineData("key")]
    [InlineData("vision")]
    [InlineData("no answer")]
    public async Task A_Limit_Smaller_Than_The_Sentence_Cuts_The_Sentence_And_Drops_The_Partial_Text(string ending)
    {
        var sink = new RecordingResponseSink(new RenderCapabilities { MaxBodyLength = 20 });
        var response = new ChatResponse(AsyncSequence.ThenThrow(ErrorOf(ending), default, "partial"));

        await Pump(Clock()).RunAsync(response, sink, CancellationToken.None);

        sink.Completed.ShouldBeTrue();
        sink.FinalText.ShouldBe(SentenceOf(ending)[..20]);
    }

    [Fact]
    public async Task A_Limit_With_No_Room_For_A_Cut_Partial_Text_Delivers_The_Sentence_Alone()
    {
        var sink = new RecordingResponseSink(
            new RenderCapabilities { MaxBodyLength = Defaults.KeyRejectedMessage.Length + 10 });
        var response = new ChatResponse(AsyncSequence.ThenThrow(ErrorOf("key"), default, Letters(50)));

        await Pump(Clock()).RunAsync(response, sink, CancellationToken.None);

        sink.Completed.ShouldBeTrue();
        sink.FinalText.ShouldBe(Defaults.KeyRejectedMessage);
    }

    [Fact]
    public async Task A_Short_Partial_Text_That_Fits_The_Room_The_Sentence_Leaves_Exactly_Is_Kept_Whole()
    {
        var limit = "partial".Length + 2 + Defaults.KeyRejectedMessage.Length;
        var sink = new RecordingResponseSink(new RenderCapabilities { MaxBodyLength = limit });
        var response = new ChatResponse(AsyncSequence.ThenThrow(ErrorOf("key"), default, "partial"));

        await Pump(Clock()).RunAsync(response, sink, CancellationToken.None);

        sink.FinalText.ShouldBe("partial\n\n" + Defaults.KeyRejectedMessage);
    }

    public static TheoryData<string, string> EveryEndingOverEverySinkThatNeverFinishes()
    {
        var rows = new TheoryData<string, string>();
        foreach (var ending in new[] { "rate limit", "quota", "credit ran out", "plan usage limit", "sign-in", "key", "model not found", "vision", "no answer", "failure" })
        {
            foreach (var sink in new[] { HonoursItsToken, IgnoresItsToken })
            {
                rows.Add(ending, sink);
            }
        }

        return rows;
    }

    [Theory]
    [MemberData(nameof(EveryEndingOverEverySinkThatNeverFinishes))]
    public void An_Error_Ending_The_Sink_Never_Finishes_Ends_The_Run_Once_The_Overall_Timeout_Has_Passed(
        string ending, string sinkKind)
    {
        using var inline = new InlineContinuations();
        var time = Clock();
        var logger = new RecordingLogger();
        var sink = new TerminalSink(NeverFinishes(sinkKind));
        var response = new ChatResponse(AtOnce(ErrorOf(ending), "partial"));

        var run = TimedPump(time, logger).RunAsync(response, sink, CancellationToken.None);

        sink.TerminalCount.ShouldBe(1);
        time.Advance(Ceiling - OneSecond);
        sink.Token.IsCancellationRequested.ShouldBeFalse("the write has what remains of the overall timeout");
        run.IsCompleted.ShouldBeFalse();
        time.Advance(OneSecond);
        sink.Token.IsCancellationRequested.ShouldBeTrue();
        run.Status.ShouldBe(
            TaskStatus.RanToCompletion, $"RunAsync must return once OverallTimeout has passed, also when the sink {sinkKind}");
        var undelivered = UndeliveredEntry(logger);
        undelivered.Level.ShouldBe(LogLevel.Warning);
        undelivered.Message.ShouldContain("timed out");
        undelivered.Message.ShouldNotContain("partial");
        undelivered.Exception.ShouldBeNull();
    }

    [Theory]
    [InlineData("timeout", HonoursItsToken)]
    [InlineData("timeout", IgnoresItsToken)]
    [InlineData("rate limit", HonoursItsToken)]
    [InlineData("rate limit", IgnoresItsToken)]
    public void A_Terminal_Write_That_Starts_After_The_Timeout_Ends_The_Run_Once_The_Terminal_Write_Grace_Has_Passed(
        string ending, string sinkKind)
    {
        using var inline = new InlineContinuations();
        var time = Clock();
        var logger = new RecordingLogger();
        var sink = new TerminalSink(NeverFinishes(sinkKind));
        var response = new ChatResponse(
            EndsAfterTheTimeout(time, ending == "timeout" ? null : ErrorOf(ending)));

        var run = TimedPump(time, logger).RunAsync(response, sink, CancellationToken.None);

        sink.TerminalCount.ShouldBe(1);
        sink.Failed.ShouldBe(ending == "timeout");
        time.Advance(Grace - OneSecond);
        sink.Token.IsCancellationRequested.ShouldBeFalse("a write that starts after the timeout has the grace");
        run.IsCompleted.ShouldBeFalse();
        time.Advance(OneSecond);
        sink.Token.IsCancellationRequested.ShouldBeTrue();
        run.Status.ShouldBe(
            TaskStatus.RanToCompletion,
            $"RunAsync must return once TerminalWriteGrace has passed after the timeout, also when the sink {sinkKind}");
        var undelivered = UndeliveredEntry(logger);
        undelivered.Message.ShouldContain("timed out");
        undelivered.Message.ShouldNotContain("partial");
        undelivered.Exception.ShouldBeNull();
    }

    [Theory]
    [InlineData(false, HonoursItsToken)]
    [InlineData(false, IgnoresItsToken)]
    [InlineData(true, HonoursItsToken)]
    [InlineData(true, IgnoresItsToken)]
    public void The_Callers_Cancellation_Stops_A_Run_Whose_Terminal_Write_The_Sink_Never_Finishes(
        bool afterTheTimeout, string sinkKind)
    {
        using var inline = new InlineContinuations();
        var time = Clock();
        var sink = new TerminalSink(NeverFinishes(sinkKind));
        using var caller = new CancellationTokenSource();
        var response = new ChatResponse(
            afterTheTimeout ? EndsAfterTheTimeout(time, null) : AtOnce(ErrorOf("rate limit"), "partial"));

        var run = TimedPump(time, new RecordingLogger()).RunAsync(response, sink, caller.Token);

        sink.TerminalCount.ShouldBe(1);
        run.IsCompleted.ShouldBeFalse();
        caller.Cancel();

        run.Status.ShouldBe(TaskStatus.Canceled, "the caller's cancellation stops the run and propagates");
    }

    [Theory]
    [InlineData("rate limit")]
    [InlineData("failure")]
    public async Task A_Caller_Already_Cancelled_When_The_Stream_Fails_Gets_Its_Cancellation_And_No_Terminal_Call(
        string ending)
    {
        var sink = new RecordingResponseSink(FinalOnly);
        using var caller = new CancellationTokenSource();
        var response = new ChatResponse(CancelsThenThrows(caller, ErrorOf(ending)));

        await Should.ThrowAsync<OperationCanceledException>(
            () => Pump(Clock()).RunAsync(response, sink, caller.Token));

        sink.TerminalCount.ShouldBe(0);
    }

    [Fact]
    public void A_Write_Abandoned_At_The_Bound_That_Fails_Later_Is_Never_Reported_As_Unobserved()
    {
        var unobserved = new System.Collections.Concurrent.ConcurrentBag<Exception>();
        void Collect(object? sender, UnobservedTaskExceptionEventArgs e)
        {
            if (e.Exception.InnerExceptions.Any(inner => inner.Message == LateFailure))
            {
                unobserved.Add(e.Exception);
            }
        }

        TaskScheduler.UnobservedTaskException += Collect;
        try
        {
            AbandonAWriteThatFailsLater();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            GC.WaitForPendingFinalizers();

            unobserved.ShouldBeEmpty();
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= Collect;
        }
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(-5000)]
    public void A_Negative_Or_Infinite_Terminal_Write_Grace_Is_Refused_When_The_Pump_Is_Built(int milliseconds)
    {
        var options = new StreamPumpOptions { TerminalWriteGrace = TimeSpan.FromMilliseconds(milliseconds) };

        var refused = Should.Throw<ArgumentOutOfRangeException>(() => new StreamPump(options));

        refused.Message.ShouldContain(nameof(StreamPumpOptions.TerminalWriteGrace));
    }

    [Fact]
    public async Task A_Terminal_Message_The_Sink_Throws_On_Before_Returning_A_Task_Is_Logged_Once_And_The_Run_Ends()
    {
        var logger = new RecordingLogger();
        var sink = new TerminalSink(_ => throw new InvalidOperationException("msg_too_long: partial"));
        var response = new ChatResponse(AsyncSequence.ThenThrow(ErrorOf("rate limit"), default, "partial"));

        await Should.NotThrowAsync(
            () => TimedPump(Clock(), logger).RunAsync(response, sink, CancellationToken.None));

        sink.TerminalCount.ShouldBe(1);
        UndeliveredEntry(logger).Message.ShouldContain(nameof(InvalidOperationException));
    }

    [Theory]
    [InlineData("rate limit")]
    [InlineData("failure")]
    public async Task A_Terminal_Message_The_Transport_Rejects_Is_Logged_Once_And_The_Run_Ends(string ending)
    {
        var time = Clock();
        var logger = new RecordingLogger();
        var sink = new TerminalSink(
            _ => Task.FromException(new InvalidOperationException("msg_too_long: partial")));
        var response = new ChatResponse(AsyncSequence.ThenThrow(ErrorOf(ending), default, "partial"));

        await Should.NotThrowAsync(
            () => TimedPump(time, logger).RunAsync(response, sink, CancellationToken.None));

        sink.TerminalCount.ShouldBe(1);
        var undelivered = logger.Entries
            .Where(e => e.Message.Contains("terminal message", StringComparison.Ordinal))
            .ShouldHaveSingleItem();
        undelivered.Level.ShouldBe(LogLevel.Warning);
        undelivered.Message.ShouldContain(nameof(InvalidOperationException));
        undelivered.Message.ShouldNotContain("partial");
        undelivered.Exception.ShouldBeNull();
    }

    private static StreamPump TimedPump(FakeTimeProvider time, ILogger logger)
        => new(new StreamPumpOptions
        {
            OverallTimeout = Ceiling,
            TerminalWriteGrace = Grace,
            TimeProvider = time,
            Logger = logger,
        });

    private static Exception ErrorOf(string ending) => ending switch
    {
        "rate limit" => new LlmRateLimitException("LLM rate limit (HTTP 429)."),
        "quota" => new LlmQuotaExceededException("credit_balance_exhausted"),
        "credit ran out" => new LlmQuotaExceededException("credit_balance_too_low"),
        "plan usage limit" => new LlmQuotaExceededException("subscription_sharing_usage_limit_exceeded"),
        "sign-in" => new LlmSignInRequiredException(),
        "key" => new LlmKeyRejectedException(),
        "model not found" => new LlmModelNotFoundException("qwen2.5:3b"),
        "vision" => new LlmVisionUnavailableException(),
        "no answer" => new LlmNoAnswerException(NoAnswerSentence),
        "failure" => new InvalidOperationException("connection reset"),
        _ => throw new ArgumentOutOfRangeException(nameof(ending), ending, null),
    };

    private static string SentenceOf(string ending) => ending switch
    {
        "rate limit" => Defaults.RateLimitMessage,
        "quota" => Defaults.QuotaExceededMessage,
        "credit ran out" => Defaults.CreditExhaustedMessage,
        "plan usage limit" => Defaults.PlanUsageLimitMessage,
        "sign-in" => Defaults.SignInRequiredMessage,
        "key" => Defaults.KeyRejectedMessage,
        "model not found" => ModelNotFoundSentence,
        "vision" => Defaults.VisionUnavailableMessage,
        "no answer" => NoAnswerSentence,
        _ => throw new ArgumentOutOfRangeException(nameof(ending), ending, null),
    };

    private static string PartialOf(string partial) => partial switch
    {
        "empty" => string.Empty,
        "short" => "partial",
        "at the limit" => Letters(Limit),
        "over the limit" => Letters(Limit * 3),
        _ => throw new ArgumentOutOfRangeException(nameof(partial), partial, null),
    };

    private static string MustEndWith(string ending, string partial)
    {
        var text = PartialOf(partial);
        if (ending != "failure")
        {
            return SentenceOf(ending);
        }

        return text.Length > Limit ? CutMarker : text[^Math.Min(20, text.Length)..];
    }

    private static string? WholeBodyOf(string ending, string partial) => (ending, partial) switch
    {
        ("failure", "empty" or "short" or "at the limit") => PartialOf(partial),
        ("vision", _) or (_, "empty") => SentenceOf(ending),
        (_, "short") => PartialOf(partial) + "\n\n" + SentenceOf(ending),
        _ => null,
    };

    private static string Letters(int count)
        => string.Concat(Enumerable.Range(0, count).Select(i => (char)('a' + (i % 26))));

    private const string HonoursItsToken = "honours its token";
    private const string IgnoresItsToken = "ignores its token";

    private static (LogLevel Level, string Message, Exception? Exception) UndeliveredEntry(RecordingLogger logger)
        => logger.Entries
            .Where(e => e.Message.Contains("terminal message", StringComparison.Ordinal))
            .ShouldHaveSingleItem();

    private static Func<CancellationToken, Task> NeverFinishes(string sinkKind) => sinkKind switch
    {
        HonoursItsToken => UntilCancelledAsync,
        IgnoresItsToken => _ => new TaskCompletionSource().Task,
        _ => throw new ArgumentOutOfRangeException(nameof(sinkKind), sinkKind, null),
    };

    private static Task UntilCancelledAsync(CancellationToken token)
    {
        var never = new TaskCompletionSource();
        token.Register(() => never.TrySetCanceled(token));
        return never.Task;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void AbandonAWriteThatFailsLater()
    {
        using var inline = new InlineContinuations();
        var time = Clock();
        var abandoned = new TaskCompletionSource();
        var sink = new TerminalSink(_ => abandoned.Task);
        var response = new ChatResponse(AtOnce(ErrorOf("rate limit"), "partial"));

        var run = TimedPump(time, new RecordingLogger()).RunAsync(response, sink, CancellationToken.None);
        time.Advance(Ceiling);

        run.Status.ShouldBe(TaskStatus.RanToCompletion);
        abandoned.SetException(new InvalidOperationException(LateFailure));
    }

    private static async IAsyncEnumerable<string> AtOnce(Exception failure, params string[] chunks)
    {
        foreach (var chunk in chunks)
        {
            yield return chunk;
        }

        await Task.CompletedTask.ConfigureAwait(false);
        throw failure;
    }

    private static async IAsyncEnumerable<string> EndsAfterTheTimeout(
        FakeTimeProvider time,
        Exception? failure,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        yield return "partial";
        time.Advance(Ceiling);
        await Task.CompletedTask.ConfigureAwait(false);
        if (failure is not null)
        {
            throw failure;
        }

        cancellationToken.ThrowIfCancellationRequested();
    }

    private static async IAsyncEnumerable<string> CancelsThenThrows(CancellationTokenSource caller, Exception failure)
    {
        yield return "partial";
        await caller.CancelAsync().ConfigureAwait(false);
        throw failure;
    }

    private sealed class InlineContinuations : IDisposable
    {
        private readonly SynchronizationContext? _previous = SynchronizationContext.Current;

        public InlineContinuations() => SynchronizationContext.SetSynchronizationContext(null);

        public void Dispose() => SynchronizationContext.SetSynchronizationContext(_previous);
    }

    private sealed class TerminalSink(Func<CancellationToken, Task> terminal) : IResponseSink
    {
        public RenderCapabilities Capabilities { get; } = FinalOnly;

        public int TerminalCount { get; private set; }

        public bool Failed { get; private set; }

        public CancellationToken Token { get; private set; }

        public Task BeginAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task UpdateAsync(string cumulativeText, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task CompleteAsync(
            string finalText, IReadOnlyList<Citation> citations, CancellationToken cancellationToken)
            => EndAsync(cancellationToken);

        public Task FailAsync(string finalTextSoFar, string reason, CancellationToken cancellationToken)
        {
            Failed = true;
            return EndAsync(cancellationToken);
        }

        private Task EndAsync(CancellationToken cancellationToken)
        {
            TerminalCount++;
            Token = cancellationToken;
            return terminal(cancellationToken);
        }
    }

    // Captures (level, message, exception) per emit so tests can assert log
    // shape: quiet (no exception, no Warning) vs. error (Warning + stack).
    private sealed class RecordingLogger : ILogger
    {
        public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = [];

        public IEnumerable<(LogLevel Level, string Message, Exception? Exception)> EntriesAt(
            LogLevel level) => Entries.Where(e => e.Level == level);

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception), exception));

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();

            public void Dispose()
            {
            }
        }
    }
}
