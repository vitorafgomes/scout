using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Scout.Llm.Abstractions;

namespace Scout.Adapter.Local.Tests;

public sealed class LiveTranslationQueueTests
{
    internal sealed class GatedTranslator : ILiveTranslator
    {
        private readonly Channel<Call> _calls = Channel.CreateUnbounded<Call>();
        private readonly List<Call> _all = [];

        public Func<Call, Task<string>>? Answering { get; init; }

        public IReadOnlyList<Call> Calls
        {
            get
            {
                lock (_all)
                {
                    return [.. _all];
                }
            }
        }

        public Task<string> TranslateAsync(string text, IReadOnlyList<string> context, string targetLanguage, CancellationToken ct)
        {
            var call = new Call(text, context, targetLanguage, ct);
            lock (_all)
            {
                _all.Add(call);
            }
            if (Answering is { } answering)
            {
                return answering(call);
            }

            _calls.Writer.TryWrite(call);
            return call.Result.Task;
        }

        public async Task<Call> NextCallAsync()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            return await _calls.Reader.ReadAsync(timeout.Token);
        }
    }

    internal sealed record Call(string Text, IReadOnlyList<string> Context, string Language, CancellationToken Token)
    {
        public TaskCompletionSource<string> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Answer(string translation) => Result.SetResult(translation);

        public void Fail(Exception error) => Result.SetException(error);
    }

    private sealed class Emitted
    {
        private readonly List<LiveTranslation> _events = [];
        private TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IReadOnlyList<LiveTranslation> All
        {
            get
            {
                lock (_events)
                {
                    return [.. _events];
                }
            }
        }

        public void Add(LiveTranslation translation)
        {
            TaskCompletionSource changed;
            lock (_events)
            {
                _events.Add(translation);
                changed = _changed;
                _changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
            changed.TrySetResult();
        }

        public async Task<LiveTranslation> WaitForAsync(Func<LiveTranslation, bool> match)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (true)
            {
                Task changed;
                lock (_events)
                {
                    if (_events.FirstOrDefault(match) is { } found)
                    {
                        return found;
                    }
                    changed = _changed.Task;
                }
                try
                {
                    await changed.WaitAsync(timeout.Token);
                }
                catch (OperationCanceledException)
                {
                    throw new ShouldAssertException($"No matching translation within 10 s. Emitted: {string.Join(", ", All)}");
                }
            }
        }
    }

    private static LiveTranslationQueue Queue(ILiveTranslator translator, Emitted emitted, TimeProvider? time = null)
        => new("l-1", translator, Task.FromResult("en"), emitted.Add, time ?? new FakeTimeProvider(), NullLogger.Instance, CancellationToken.None);

    private static Func<Call, Task<string>> Upper => call => Task.FromResult(call.Text.ToUpperInvariant());

    [Fact]
    public async Task While_One_Translation_Is_In_Flight_Only_The_Newest_Final_Waits_And_The_Skipped_Ones_Say_So()
    {
        var translator = new GatedTranslator();
        var emitted = new Emitted();
        var queue = Queue(translator, emitted);

        queue.Enqueue(1, "one");
        var first = await translator.NextCallAsync();
        queue.Enqueue(2, "two");
        queue.Enqueue(3, "three");
        queue.Enqueue(4, "four");
        var whileInFlight = translator.Calls.Count;
        first.Answer("ONE");
        var newest = await translator.NextCallAsync();
        newest.Answer("FOUR");
        await emitted.WaitForAsync(e => e.Seq == 4);

        whileInFlight.ShouldBe(1, "a second translation never starts while one is in flight");
        newest.Text.ShouldBe("four", "only the newest final waited for the one in flight");
        translator.Calls.Select(c => c.Text).ShouldBe(["one", "four"]);
        emitted.All.ShouldBe(
        [
            new LiveTranslation(1, "en", "ONE", null),
            new LiveTranslation(2, "en", null, "skipped"),
            new LiveTranslation(3, "en", null, "skipped"),
            new LiveTranslation(4, "en", "FOUR", null),
        ]);
    }

    [Fact]
    public async Task The_Two_Previous_Finals_Go_As_Context()
    {
        var translator = new GatedTranslator();
        var emitted = new Emitted();
        var queue = Queue(translator, emitted);

        queue.Enqueue(1, "one");
        var first = await translator.NextCallAsync();
        queue.Enqueue(2, "two");
        queue.Enqueue(3, "three");
        first.Answer("ONE");
        var third = await translator.NextCallAsync();
        third.Answer("THREE");
        await emitted.WaitForAsync(e => e.Seq == 3);
        queue.Enqueue(4, "four");
        var fourth = await translator.NextCallAsync();
        fourth.Answer("FOUR");
        await emitted.WaitForAsync(e => e.Seq == 4);

        first.Context.ShouldBeEmpty("the first final of a session has no previous line");
        third.Context.ShouldBe(["one", "two"], "the two previous finals are context, the skipped one included");
        fourth.Context.ShouldBe(["two", "three"], "only the two finals right before it");
    }

    [Fact]
    public async Task Past_20_Lines_A_Minute_Translation_Pauses_With_One_Note_And_Resumes()
    {
        var time = new FakeTimeProvider();
        var translator = new GatedTranslator { Answering = Upper };
        var emitted = new Emitted();
        var queue = Queue(translator, emitted, time);

        for (var seq = 1; seq <= 20; seq++)
        {
            queue.Enqueue(seq, $"line {seq}");
            await emitted.WaitForAsync(e => e.Seq == seq);
        }
        queue.Enqueue(21, "line 21");
        var paused = await emitted.WaitForAsync(e => e.Seq == 0);
        queue.Enqueue(22, "line 22");
        time.Advance(TimeSpan.FromSeconds(59));
        var callsAfter59Seconds = translator.Calls.Count;
        time.Advance(TimeSpan.FromSeconds(1));
        var resumed = await emitted.WaitForAsync(e => e.Seq == 22);

        paused.ShouldBe(new LiveTranslation(0, "en", null, "Translation paused: more than 20 lines a minute."));
        callsAfter59Seconds.ShouldBe(20, "no line is translated while 20 started in the last minute");
        resumed.ShouldBe(new LiveTranslation(22, "en", "LINE 22", null), "translation resumes once the minute allows");
        emitted.All.Count(e => e.Seq == 0).ShouldBe(1, "the pause is noted once, though two lines waited during it");
        emitted.All.ShouldContain(new LiveTranslation(21, "en", null, "skipped"));
        translator.Calls.Count.ShouldBe(21);
    }

    [Fact]
    public async Task A_Failing_Translation_Emits_The_Error_And_The_Queue_Keeps_Going()
    {
        var time = new FakeTimeProvider();
        var translator = new GatedTranslator();
        var emitted = new Emitted();
        var queue = Queue(translator, emitted, time);

        queue.Enqueue(1, "one");
        (await translator.NextCallAsync()).Fail(new InvalidOperationException("provider down"));
        await emitted.WaitForAsync(e => e.Seq == 1);
        queue.Enqueue(2, "two");
        (await translator.NextCallAsync()).Fail(new LlmRateLimitException("429"));
        await emitted.WaitForAsync(e => e.Seq == 2);
        queue.Enqueue(3, "three");
        var slow = await translator.NextCallAsync();
        time.Advance(TimeSpan.FromSeconds(20));
        await emitted.WaitForAsync(e => e.Seq == 3);
        queue.Enqueue(4, "four");
        (await translator.NextCallAsync()).Answer("FOUR");
        await emitted.WaitForAsync(e => e.Seq == 4);

        slow.Token.IsCancellationRequested.ShouldBeTrue("a call past its 20 s budget is cancelled");
        emitted.All.ShouldBe(
        [
            new LiveTranslation(1, "en", null, "Scout could not translate that line; see Scout logs."),
            new LiveTranslation(2, "en", null, "I've hit the model's rate limit right now — please wait a moment and try again."),
            new LiveTranslation(3, "en", null, "Scout could not translate that line; see Scout logs."),
            new LiveTranslation(4, "en", "FOUR", null),
        ]);
    }

    [Fact]
    public async Task Drain_Waits_For_The_In_Flight_Translation_At_Most_15_s()
    {
        var time = new FakeTimeProvider();
        var answered = new GatedTranslator();
        var answeredEvents = new Emitted();
        var waited = Queue(answered, answeredEvents, time);
        var hung = new GatedTranslator();
        var hungEvents = new Emitted();
        var abandoned = Queue(hung, hungEvents, time);

        waited.Enqueue(1, "one");
        var inFlight = await answered.NextCallAsync();
        var drain = waited.DrainAsync(TimeSpan.FromSeconds(15));
        var drainedBeforeTheAnswer = drain.IsCompleted;
        inFlight.Answer("ONE");
        await drain.WaitAsync(TimeSpan.FromSeconds(10));

        abandoned.Enqueue(1, "one");
        var stuck = await hung.NextCallAsync();
        abandoned.Enqueue(2, "two");
        var giveUp = abandoned.DrainAsync(TimeSpan.FromSeconds(15));
        time.Advance(TimeSpan.FromSeconds(14));
        var drainedAt14Seconds = giveUp.IsCompleted;
        time.Advance(TimeSpan.FromSeconds(1));
        await giveUp.WaitAsync(TimeSpan.FromSeconds(10));
        abandoned.Enqueue(3, "three");

        drainedBeforeTheAnswer.ShouldBeFalse("the drain waits for the translation in flight");
        answeredEvents.All.ShouldBe([new LiveTranslation(1, "en", "ONE", null)]);
        drainedAt14Seconds.ShouldBeFalse("the drain waits up to 15 s");
        stuck.Token.IsCancellationRequested.ShouldBeTrue("after 15 s the translation in flight is cancelled");
        hungEvents.All.ShouldBe(
        [
            new LiveTranslation(1, "en", null, "skipped"),
            new LiveTranslation(2, "en", null, "skipped"),
        ], "the lines left untranslated are reported as gaps, and nothing follows the drain");
        hung.Calls.Count.ShouldBe(1, "a final queued after the drain is never translated");
    }

    [Fact]
    public async Task Drain_While_Paused_Does_Not_Wait()
    {
        var time = new FakeTimeProvider();
        var translator = new GatedTranslator { Answering = Upper };
        var emitted = new Emitted();
        var queue = Queue(translator, emitted, time);
        for (var seq = 1; seq <= 20; seq++)
        {
            queue.Enqueue(seq, $"line {seq}");
            await emitted.WaitForAsync(e => e.Seq == seq);
        }
        queue.Enqueue(21, "line 21");
        await emitted.WaitForAsync(e => e.Seq == 0);

        var drain = queue.DrainAsync(TimeSpan.FromSeconds(15));
        var drained = await Task.WhenAny(drain, Task.Delay(TimeSpan.FromSeconds(5))) == drain;

        drained.ShouldBeTrue("a drain while paused at the cap completes within 0 s, with the fake clock never advanced");
        emitted.All[^1].ShouldBe(new LiveTranslation(21, "en", null, "skipped"), "the line waiting out the pause is written as skipped");
        translator.Calls.Count.ShouldBe(20, "nothing is translated while paused");
    }

    [Fact]
    public async Task A_Final_After_Drain_Started_Is_Refused_And_Writes_Nothing()
    {
        var time = new FakeTimeProvider();
        var translator = new GatedTranslator();
        var emitted = new Emitted();
        var queue = Queue(translator, emitted, time);
        queue.Enqueue(1, "one");
        var inFlight = await translator.NextCallAsync();

        var drain = queue.DrainAsync(TimeSpan.FromSeconds(15));
        queue.Enqueue(2, "two");
        inFlight.Answer("ONE");
        await emitted.WaitForAsync(e => e.Seq == 1);
        time.Advance(TimeSpan.FromSeconds(15));
        await drain.WaitAsync(TimeSpan.FromSeconds(10));

        translator.Calls.Select(c => c.Text).ShouldBe(["one"], "a final that arrives once the drain started is never translated");
        emitted.All.ShouldBe([new LiveTranslation(1, "en", "ONE", null)], "the refused final writes nothing, not even skipped");
    }
}
