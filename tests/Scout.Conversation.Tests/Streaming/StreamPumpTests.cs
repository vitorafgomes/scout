using System.Runtime.CompilerServices;
using Microsoft.Extensions.Time.Testing;
using Scout.Conversation.Abstractions;
using Scout.Conversation.Streaming;
using Scout.Conversation.Tests.Fakes;
using Scout.Llm.Abstractions;

namespace Scout.Conversation.Tests.Streaming;

public sealed class StreamPumpTests
{
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(750);
    private const string Cursor = "▌";

    private static readonly RenderCapabilities FineGrained = new()
    {
        FineGrainedEdit = true,
        MaxBodyLength = 4096,
    };

    private static StreamPump Pump(
        FakeTimeProvider time,
        int? maxBodyLength = null,
        string? placeholder = null,
        TimeSpan? overallTimeout = null)
        => new(new StreamPumpOptions
        {
            EditInterval = Interval,
            Cursor = Cursor,
            MaxBodyLength = maxBodyLength,
            EmptyStreamPlaceholder = placeholder ?? "(no response from model)",
            OverallTimeout = overallTimeout,
            TimeProvider = time,
        });

    private static FakeTimeProvider Clock() =>
        new(new DateTimeOffset(2026, 5, 28, 0, 0, 0, TimeSpan.Zero));

    // Token sequence that advances the fake clock by `step` before each chunk,
    // so interim-edit cadence is deterministic.
    private static async IAsyncEnumerable<string> Ticking(
        FakeTimeProvider time,
        TimeSpan step,
        [EnumeratorCancellation] CancellationToken cancellationToken,
        params string[] chunks)
    {
        foreach (var chunk in chunks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            time.Advance(step);
            await Task.Yield();
            yield return chunk;
        }
    }

    [Fact]
    public async Task RunAsync_FineGrained_FollowsLifecycle_AndCompletes()
    {
        var time = Clock();
        var sink = new RecordingResponseSink(FineGrained);
        var response = new ChatResponse(AsyncSequence.Of(default, "Hello", " ", "world"));

        await Pump(time).RunAsync(response, sink, CancellationToken.None);

        sink.BeginCount.ShouldBe(1);
        sink.TerminalCount.ShouldBe(1);
        sink.Completed.ShouldBeTrue();
        sink.Failed.ShouldBeFalse();
        sink.FinalText.ShouldBe("Hello world");
    }

    [Fact]
    public async Task RunAsync_Interim_Updates_Are_Cumulative_Monotonic_With_Cursor()
    {
        var time = Clock();
        var sink = new RecordingResponseSink(FineGrained);
        // Advance one interval before each chunk so every chunk triggers an edit.
        var response = new ChatResponse(
            Ticking(time, Interval, default, "a", "b", "c"));

        await Pump(time).RunAsync(response, sink, CancellationToken.None);

        sink.Updates.ShouldNotBeEmpty();
        // Each update ends with the cursor; cumulative text grows monotonically.
        var stripped = sink.Updates.Select(u => u.Replace(Cursor, string.Empty)).ToList();
        for (var i = 1; i < stripped.Count; i++)
        {
            stripped[i].Length.ShouldBeGreaterThanOrEqualTo(stripped[i - 1].Length);
            stripped[i].ShouldStartWith(stripped[i - 1]);
        }
        sink.Updates[^1].ShouldEndWith(Cursor);
        sink.FinalText.ShouldBe("abc");
    }

    [Fact]
    public async Task RunAsync_Coalesces_Rapid_Chunks_Within_One_Window()
    {
        var time = Clock();
        var sink = new RecordingResponseSink(FineGrained);
        // No clock advance between chunks -> only the first chunk edits.
        var response = new ChatResponse(AsyncSequence.Of(default, "1", "2", "3", "4", "5"));

        await Pump(time).RunAsync(response, sink, CancellationToken.None);

        // First chunk edits immediately; the rest coalesce (no interval elapsed).
        sink.Updates.Count.ShouldBe(1);
        sink.Updates[0].ShouldBe("1" + Cursor);
        sink.FinalText.ShouldBe("12345");
    }

    [Fact]
    public async Task RunAsync_Truncates_Tail_Preserving_To_MaxBodyLength()
    {
        const int max = 64;
        var time = Clock();
        var sink = new RecordingResponseSink(new RenderCapabilities
        {
            FineGrainedEdit = true,
            MaxBodyLength = max,
        });
        var longText = new string('a', 200);
        var response = new ChatResponse(AsyncSequence.Of(default, longText));

        await Pump(time).RunAsync(response, sink, CancellationToken.None);

        var interim = sink.Updates[0];
        interim.Length.ShouldBeLessThanOrEqualTo(max);
        interim.ShouldStartWith("... (truncated)\n\n");
        interim.ShouldEndWith("a" + Cursor);
        sink.FinalText!.Length.ShouldBeLessThanOrEqualTo(max);
    }

    [Fact]
    public async Task RunAsync_FinalOnly_Degradation_Skips_Interim_Updates()
    {
        var time = Clock();
        var sink = new RecordingResponseSink(new RenderCapabilities
        {
            FineGrainedEdit = false,
            SupportsInterimCitations = false,
            MaxBodyLength = 4096,
        });
        var response = new ChatResponse(
            Ticking(time, Interval, default, "a", "b", "c"));

        await Pump(time).RunAsync(response, sink, CancellationToken.None);

        sink.Updates.ShouldBeEmpty();
        sink.Completed.ShouldBeTrue();
        sink.FinalText.ShouldBe("abc");
    }

    [Fact]
    public async Task RunAsync_Empty_Stream_Completes_With_Placeholder()
    {
        var time = Clock();
        var sink = new RecordingResponseSink(FineGrained);
        var response = new ChatResponse(AsyncSequence.Of(default));

        await Pump(time, placeholder: "(empty)").RunAsync(response, sink, CancellationToken.None);

        sink.Completed.ShouldBeTrue();
        sink.Updates.ShouldBeEmpty();
        sink.FinalText.ShouldBe("(empty)");
    }

    [Fact]
    public async Task RunAsync_Forwards_Citations_On_Complete()
    {
        var time = Clock();
        var sink = new RecordingResponseSink(FineGrained);
        var citations = new[] { new Citation("src/A.cs") };
        var response = new ChatResponse(AsyncSequence.Of(default, "x"), citations);

        await Pump(time).RunAsync(response, sink, CancellationToken.None);

        sink.FinalCitations.Count.ShouldBe(1);
        sink.FinalCitations[0].Label.ShouldBe("src/A.cs");
    }

    // A stream that yields one chunk then waits past any timeout. The wait is
    // driven by the same fake clock, so the linked timeout CTS fires first.
    private static async IAsyncEnumerable<string> HangsAfterFirst(
        FakeTimeProvider time,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        yield return "partial";
        await Task.Delay(TimeSpan.FromHours(1), time, cancellationToken).ConfigureAwait(false);
        yield return "never";
    }

    [Fact]
    public async Task RunAsync_Sink_MaxResponseDuration_Finalizes_Before_Global_Timeout()
    {
        var time = Clock();
        // Global timeout is generous; the sink declares a tighter 2s cap, which
        // must win and trigger finalize.
        var sink = new RecordingResponseSink(new RenderCapabilities
        {
            FineGrainedEdit = true,
            MaxBodyLength = 4096,
            MaxResponseDuration = TimeSpan.FromSeconds(2),
        });
        var response = new ChatResponse(HangsAfterFirst(time));

        var run = Pump(time, overallTimeout: TimeSpan.FromMinutes(5))
            .RunAsync(response, sink, CancellationToken.None);

        // Advance just past the sink cap but well short of the global timeout.
        while (!run.IsCompleted)
        {
            await Task.Yield();
            time.Advance(TimeSpan.FromMilliseconds(500));
            if (time.GetUtcNow() - new DateTimeOffset(2026, 5, 28, 0, 0, 0, TimeSpan.Zero)
                > TimeSpan.FromSeconds(10))
            {
                break;
            }
        }
        await run;

        // The sink cap (2s) fired long before the 5min global timeout.
        sink.Failed.ShouldBeTrue();
        sink.Completed.ShouldBeFalse();
        sink.FinalText.ShouldBe("partial");
        sink.FailReason.ShouldNotBeNull();
        sink.FailReason!.ShouldContain("timed out");
    }

    [Fact]
    public async Task RunAsync_OverallTimeout_Bounds_Run_And_Finalizes_With_Partial()
    {
        var time = Clock();
        var sink = new RecordingResponseSink(FineGrained);
        var response = new ChatResponse(HangsAfterFirst(time));

        var run = Pump(time, overallTimeout: TimeSpan.FromSeconds(3))
            .RunAsync(response, sink, CancellationToken.None);

        while (!run.IsCompleted)
        {
            await Task.Yield();
            time.Advance(TimeSpan.FromMilliseconds(500));
        }
        await run;

        sink.BeginCount.ShouldBe(1);
        sink.Failed.ShouldBeTrue();
        sink.Completed.ShouldBeFalse();
        sink.FinalText.ShouldBe("partial");
    }

    [Fact]
    public async Task RunAsync_Throws_When_Response_Null()
    {
        var sink = new RecordingResponseSink(FineGrained);
        await Should.ThrowAsync<ArgumentNullException>(
            () => Pump(Clock()).RunAsync(null!, sink, CancellationToken.None));
    }

    [Fact]
    public async Task RunAsync_Throws_When_Sink_Null()
    {
        var response = new ChatResponse(AsyncSequence.Of(default, "x"));
        await Should.ThrowAsync<ArgumentNullException>(
            () => Pump(Clock()).RunAsync(response, null!, CancellationToken.None));
    }

    private const string FindingsBlock = "```scout-findings\n{\"repository\":\"octocat/hello\",\"number\":5}\n```";

    private static async IAsyncEnumerable<string> Attaching(
        FakeTimeProvider time,
        string block,
        Exception? failure,
        params string[] chunks)
    {
        foreach (var (chunk, index) in chunks.Select((c, i) => (c, i)))
        {
            if (index == 1)
            {
                TurnAttachments.Attach(block);
            }

            time.Advance(Interval);
            await Task.Yield();
            yield return chunk;
        }

        if (failure is not null)
        {
            throw failure;
        }
    }

    [Fact]
    public async Task An_Attached_Block_Is_Appended_To_The_Final_Reply_After_The_Model_Text()
    {
        var time = Clock();
        var sink = new RecordingResponseSink(FineGrained);
        using var turn = TurnAttachments.Begin();

        await Pump(time).RunAsync(
            new ChatResponse(Attaching(time, FindingsBlock, null, "Two findings. ", "Verdict: needs rework.\n")), sink, CancellationToken.None);

        sink.Completed.ShouldBeTrue();
        sink.FinalText.ShouldBe("Two findings. Verdict: needs rework.\n\n" + FindingsBlock);
        TurnAttachments.Take().ShouldBeEmpty();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_Failed_Turn_Appends_No_Block(bool rateLimited)
    {
        var time = Clock();
        var sink = new RecordingResponseSink(FineGrained);
        using var turn = TurnAttachments.Begin();
        Exception failure = rateLimited ? new LlmRateLimitException("429") : new InvalidOperationException("provider down");

        await Pump(time).RunAsync(
            new ChatResponse(Attaching(time, FindingsBlock, failure, "Two findings. ", "Verdict")), sink, CancellationToken.None);

        sink.TerminalCount.ShouldBe(1);
        sink.Failed.ShouldBe(!rateLimited);
        sink.FinalText.ShouldNotBeNull().ShouldStartWith("Two findings. Verdict");
        sink.FinalText.ShouldNotContain("scout-findings");
    }

    [Fact]
    public async Task Interim_Updates_Carry_No_Block()
    {
        var time = Clock();
        var sink = new RecordingResponseSink(FineGrained);
        using var turn = TurnAttachments.Begin();

        await Pump(time).RunAsync(
            new ChatResponse(Attaching(time, FindingsBlock, null, "Two ", "findings, ", "verdict.")), sink, CancellationToken.None);

        sink.Updates.Count.ShouldBe(3);
        sink.Updates.ShouldAllBe(update => !update.Contains("scout-findings"));
        sink.FinalText.ShouldBe("Two findings, verdict.\n\n" + FindingsBlock);
    }

    private const string ModelWithItsOwnFence =
        "Two findings.\n\n```scout-findings\n{\"repository\":\"octocat/hello\",\"number\":4}\n```\n\nVerdict: needs rework.\n";

    [Fact]
    public async Task A_Model_Written_Findings_Fence_Gives_Way_To_The_Attached_Block_And_The_Prose_Around_It_Stays()
    {
        var time = Clock();
        var sink = new RecordingResponseSink(FineGrained);
        using var turn = TurnAttachments.Begin();

        await Pump(time).RunAsync(
            new ChatResponse(Attaching(time, FindingsBlock, null, ModelWithItsOwnFence[..20], ModelWithItsOwnFence[20..])), sink, CancellationToken.None);

        var final = sink.FinalText.ShouldNotBeNull();
        final.Split("```scout-findings").Length.ShouldBe(2, "exactly one scout-findings fence");
        final.ShouldStartWith("Two findings.");
        final.ShouldContain("Verdict: needs rework.");
        final.ShouldNotContain("\"number\":4");
        final.ShouldEndWith("Verdict: needs rework.\n\n" + FindingsBlock);
    }

    [Fact]
    public async Task Without_An_Attached_Block_The_Model_Text_Keeps_Its_Own_Findings_Fence()
    {
        var time = Clock();
        var sink = new RecordingResponseSink(FineGrained);
        using var turn = TurnAttachments.Begin();

        await Pump(time).RunAsync(
            new ChatResponse(AsyncSequence.Of(default, ModelWithItsOwnFence[..20], ModelWithItsOwnFence[20..])), sink, CancellationToken.None);

        sink.FinalText.ShouldBe(ModelWithItsOwnFence);
    }

    [Fact]
    public async Task A_Reply_Too_Long_For_The_Chat_Keeps_The_Whole_Block_And_Cuts_The_Prose()
    {
        var time = Clock();
        var sink = new RecordingResponseSink(new RenderCapabilities { FineGrainedEdit = true, MaxBodyLength = 120 });
        using var turn = TurnAttachments.Begin();
        var prose = new string('p', 200) + " end of the review.";

        await Pump(time).RunAsync(new ChatResponse(Attaching(time, FindingsBlock, null, prose[..100], prose[100..])), sink, CancellationToken.None);

        var final = sink.FinalText.ShouldNotBeNull();
        final.Length.ShouldBe(120);
        final.ShouldEndWith(" end of the review.\n\n" + FindingsBlock);
        final.ShouldStartWith("... (truncated)");
    }

    [Fact]
    public async Task A_Block_Larger_Than_The_Chat_Allows_Is_Dropped_And_The_Prose_Is_Cut_As_Before()
    {
        var time = Clock();
        var sink = new RecordingResponseSink(new RenderCapabilities { FineGrainedEdit = true, MaxBodyLength = 40 });
        using var turn = TurnAttachments.Begin();
        var prose = new string('p', 200) + " end of the review.";

        await Pump(time).RunAsync(new ChatResponse(Attaching(time, FindingsBlock, null, prose[..100], prose[100..])), sink, CancellationToken.None);

        sink.FinalText.ShouldBe("... (truncated)\n\n" + prose[^23..]);
    }

    [Fact]
    public async Task A_Model_Text_Ending_Inside_Its_Own_Unclosed_Findings_Fence_Leaves_Only_The_Attached_Block()
    {
        var time = Clock();
        var sink = new RecordingResponseSink(FineGrained);
        using var turn = TurnAttachments.Begin();
        const string Model = "Two findings. Verdict: needs rework.\n\n```scout-findings\n{\"repository\":\"octocat/hello\",\"number\":4,";

        await Pump(time).RunAsync(new ChatResponse(Attaching(time, FindingsBlock, null, Model[..20], Model[20..])), sink, CancellationToken.None);

        sink.FinalText.ShouldBe("Two findings. Verdict: needs rework.\n\n" + FindingsBlock);
    }
}
