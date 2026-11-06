using System.Globalization;
using System.Text.Json;
using Scout.Conversation.Abstractions;
using static Scout.Adapter.Local.Tests.LiveTranslationQueueTests;

namespace Scout.Adapter.Local.Tests;

public sealed class LocalLiveSessionTests
{
    private static Task NoAsk(ChatMessage message, IResponseSink sink, CancellationToken ct)
        => throw new InvalidOperationException("a live session never becomes an ask");

    private static string Listen(string id) => JsonSerializer.Serialize(new { type = "listen", id });

    private static string Audio(string id, byte[] pcm16)
        => JsonSerializer.Serialize(new { type = "audio", id, pcm16 = Convert.ToBase64String(pcm16) });

    private static string Stop(string id) => JsonSerializer.Serialize(new { type = "stop", id });

    private static string Listen(string id, string translate) => JsonSerializer.Serialize(new { type = "listen", id, translate });

    private static Func<JsonElement, bool> Seq(int seq) => e => e.GetProperty("seq").GetInt32() == seq;

    private static (int Seq, string? Language, string? Text, string? Error) Translation(JsonElement e)
        => (e.GetProperty("seq").GetInt32(),
            e.GetProperty("language").GetString(),
            e.TryGetProperty("text", out var text) ? text.GetString() : null,
            e.TryGetProperty("error", out var error) ? error.GetString() : null);

    private static IReadOnlyList<JsonElement> EventsOf(LocalChatAdapterTests.Harness harness, string id, string kind)
        => [.. harness.Stdout.Events.Where(e => LineCapture.IdOf(e) == id && e.GetProperty("event").GetString() == kind)];

    private sealed class FakeLive : ILiveTranscriber
    {
        private readonly object _gate = new();
        private readonly List<(int Session, byte[] Pcm16)> _fed = [];

        public string? PartialOnFeed { get; init; }

        public string? FinalOnStop { get; init; }

        public Func<int, string>? FinalOnFeed { get; init; }

        public bool TextsNameTheSession { get; init; }

        public bool StopFails { get; init; }

        public bool FeedFails { get; init; }

        public int? FeedFailsInSession { get; init; }

        public int? FeedFailsOnFeed { get; init; }

        public int Starts { get; private set; }

        public IReadOnlyList<byte[]> Fed
        {
            get
            {
                lock (_gate)
                {
                    return [.. _fed.Select(f => f.Pcm16)];
                }
            }
        }

        public IReadOnlyList<byte[]> FedTo(int session)
        {
            lock (_gate)
            {
                return [.. _fed.Where(f => f.Session == session).Select(f => f.Pcm16)];
            }
        }

        public ILiveSession Start(Func<LiveTranscript, Task> onEvent, CancellationToken cancellationToken)
        {
            Starts++;
            return new Session(this, Starts, onEvent);
        }

        private string Text(string text, int session) => TextsNameTheSession ? $"{text} in session {session}" : text;

        private sealed class Session(FakeLive owner, int number, Func<LiveTranscript, Task> onEvent) : ILiveSession
        {
            public async ValueTask FeedAsync(ReadOnlyMemory<byte> pcm16, CancellationToken cancellationToken)
            {
                lock (owner._gate)
                {
                    owner._fed.Add((number, pcm16.ToArray()));
                }
                if (owner.FeedFails || owner.FeedFailsInSession == number || owner.FeedFailsOnFeed == owner.FedTo(number).Count)
                {
                    throw new InvalidOperationException("the model could not decode a window");
                }
                if (owner.PartialOnFeed is { } text)
                {
                    await onEvent(new LiveTranscript(owner.Text(text, number), false));
                }
                if (owner.FinalOnFeed is { } final)
                {
                    await onEvent(new LiveTranscript(final(owner.FedTo(number).Count), true));
                }
            }

            public async Task StopAsync(CancellationToken cancellationToken)
            {
                if (owner.StopFails)
                {
                    throw new InvalidOperationException("the model could not decode");
                }
                if (owner.FinalOnStop is { } text)
                {
                    await onEvent(new LiveTranscript(owner.Text(text, number), true));
                }
            }
        }
    }

    [Fact]
    public async Task Audio_Lines_Reach_The_Transcriber_As_Pcm16_Bytes()
    {
        var live = new FakeLive();
        await using var harness = new LocalChatAdapterTests.Harness(NoAsk, live: live);
        byte[] first = [0x01, 0x80, 0xFF, 0x7F, 0x00];
        byte[] second = [0x00, 0x10, 0x20];

        harness.Send(Listen("l-1"));
        harness.Send(Audio("l-1", first));
        harness.Send(Audio("l-1", second));
        harness.Send(Stop("l-1"));
        await harness.Stdout.WaitForAsync("l-1", "complete");

        live.Fed.ShouldBe([first, second], "each audio line reaches the session as its decoded PCM16 bytes, in order");
    }

    [Fact]
    public async Task Partials_Are_Written_As_Partial_Events()
    {
        var live = new FakeLive { PartialOnFeed = "what did they", FinalOnStop = "what did they ask me?" };
        await using var harness = new LocalChatAdapterTests.Harness(NoAsk, live: live);

        harness.Send(Listen("l-1"));
        harness.Send(Audio("l-1", new byte[640]));
        var partial = await harness.Stdout.WaitForAsync("l-1", "partial");
        harness.Send(Stop("l-1"));
        var final = await harness.Stdout.WaitForAsync("l-1", "final");
        await harness.Stdout.WaitForAsync("l-1", "complete");

        partial.GetProperty("text").GetString().ShouldBe("what did they");
        final.GetProperty("text").GetString().ShouldBe("what did they ask me?");
        harness.Stdout.KindsFor("l-1").ShouldBe(["partial", "final", "complete"]);
    }

    [Fact]
    public async Task Transcript_Text_Never_Reaches_The_Log()
    {
        var logger = new RecordingLogger<LocalChatAdapter>();
        var live = new FakeLive { PartialOnFeed = "the launch code is", FinalOnStop = "the launch code is 4471" };
        await using var harness = new LocalChatAdapterTests.Harness(NoAsk, live: live, logger: logger);

        harness.Send(Listen("l-1"));
        harness.Send(Audio("l-1", new byte[640]));
        harness.Send(Stop("l-1"));
        await harness.Stdout.WaitForAsync("l-1", "complete");

        harness.Stdout.Raw.ShouldContain("the launch code is 4471", Case.Sensitive, "the transcript does reach stdout");
        lock (logger.Entries)
        {
            logger.Entries.ShouldContain(e => e.Message.Contains("l-1"), "the session is logged by its id");
            logger.Entries.ShouldNotContain(e => e.Message.Contains("launch code"));
        }
    }

    [Fact]
    public async Task Listen_Without_A_Transcriber_Fails_With_The_Sentence()
    {
        await using var harness = new LocalChatAdapterTests.Harness(NoAsk);

        harness.Send(Listen("l-1"));
        var refused = await harness.Stdout.WaitForAsync("l-1", "fail");

        refused.GetProperty("text").GetString().ShouldBe(
            "Live transcription needs a Whisper model. Download one in the Scout app under AI Sources, Local Models.");
    }

    [Fact]
    public async Task A_third_listen_is_refused_with_the_already_listening_sentence_while_two_are_open()
    {
        var live = new FakeLive();
        await using var harness = new LocalChatAdapterTests.Harness(NoAsk, live: live);

        harness.Send(Listen("l-1"));
        harness.Send(Listen("l-2"));
        harness.Send(Listen("l-3"));
        var refused = await harness.Stdout.WaitForAsync("l-3", "fail");
        harness.Send(Stop("l-1"));
        await harness.Stdout.WaitForAsync("l-1", "complete");
        harness.Send(Listen("l-4"));
        harness.Send(Stop("l-2"));
        harness.Send(Stop("l-4"));
        await harness.Stdout.WaitForAsync("l-2", "complete");
        await harness.Stdout.WaitForAsync("l-4", "complete");

        refused.GetProperty("text").GetString().ShouldBe("Scout is already listening.");
        harness.Stdout.KindsFor("l-3").ShouldBe(["fail"], "the third listen is refused and never completes");
        live.Starts.ShouldBe(3, "the third listen started nothing; a listen after one of the two stopped starts again");
    }

    [Fact]
    public async Task A_listen_with_an_id_already_open_is_refused()
    {
        var live = new FakeLive();
        await using var harness = new LocalChatAdapterTests.Harness(NoAsk, live: live);
        byte[] chunk = [0x07, 0x07];

        harness.Send(Listen("l-1"));
        harness.Send(Listen("l-1"));
        harness.Send(Audio("l-1", chunk));
        harness.Send(Stop("l-1"));
        await harness.Stdout.WaitForAsync("l-1", "complete");

        harness.Stdout.KindsFor("l-1").ShouldBe(["fail", "complete"], "the repeated listen is refused and the open session runs on");
        (await harness.Stdout.WaitForAsync("l-1", "fail")).GetProperty("text").GetString().ShouldBe("Scout is already listening.");
        live.Starts.ShouldBe(1, "the repeated listen started nothing");
        live.FedTo(1).ShouldBe([chunk], "the audio reached the session the first listen opened");
    }

    [Fact]
    public async Task Audio_and_stop_are_routed_by_id()
    {
        var live = new FakeLive();
        await using var harness = new LocalChatAdapterTests.Harness(NoAsk, live: live);
        byte[] forFirst = [0x01, 0x01];
        byte[] forSecond = [0x02, 0x01];
        byte[] forSecondAfterStop = [0x02, 0x02];

        harness.Send(Listen("l-1"));
        harness.Send(Listen("l-2"));
        harness.Send(Audio("l-2", forSecond));
        harness.Send(Audio("l-1", forFirst));
        harness.Send(Stop("l-1"));
        await harness.Stdout.WaitForAsync("l-1", "complete");
        harness.Send(Audio("l-2", forSecondAfterStop));
        harness.Send(Stop("l-2"));
        await harness.Stdout.WaitForAsync("l-2", "complete");

        live.FedTo(1).ShouldBe([forFirst], "audio for l-2 never reaches l-1's session");
        live.FedTo(2).ShouldBe([forSecond, forSecondAfterStop], "stopping l-1 leaves l-2 running and still fed");
        harness.Stdout.KindsFor("l-1").ShouldBe(["complete"]);
        harness.Stdout.KindsFor("l-2").ShouldBe(["complete"], "l-2 was never refused or stopped by l-1's stop");
    }

    [Fact]
    public async Task A_feed_that_throws_ends_only_its_own_session()
    {
        var live = new FakeLive { FeedFailsInSession = 1, PartialOnFeed = "heard" };
        await using var harness = new LocalChatAdapterTests.Harness(NoAsk, live: live);

        harness.Send(Listen("l-1"));
        harness.Send(Listen("l-2"));
        harness.Send(Audio("l-1", new byte[640]));
        var failed = await harness.Stdout.WaitForAsync("l-1", "fail");
        harness.Send(Listen("l-3"));
        harness.Send(Audio("l-2", new byte[640]));
        harness.Send(Stop("l-2"));
        harness.Send(Stop("l-3"));
        await harness.Stdout.WaitForAsync("l-3", "complete");
        await harness.Stdout.WaitForAsync("l-2", "complete");

        failed.GetProperty("text").GetString().ShouldBe("Scout could not transcribe that; see Scout logs.");
        harness.Stdout.KindsFor("l-1").ShouldBe(["fail"], "the failed session ends with its fail and never completes");
        harness.Stdout.KindsFor("l-2").ShouldBe(["partial", "complete"], "the other session keeps transcribing");
        harness.Stdout.KindsFor("l-3").ShouldBe(["complete"], "only l-1's slot was freed, so a listen beside l-2 is accepted");
        live.Starts.ShouldBe(3);
    }

    [Fact]
    public async Task Stop_of_an_unknown_id_answers_the_could_not_read_sentence_while_two_are_open()
    {
        var live = new FakeLive();
        await using var harness = new LocalChatAdapterTests.Harness(NoAsk, live: live);
        byte[] forFirst = [0x01];
        byte[] forSecond = [0x02];

        harness.Send(Listen("l-1"));
        harness.Send(Listen("l-2"));
        harness.Send(Stop("l-9"));
        var refused = await harness.Stdout.WaitForAsync("l-9", "fail");
        harness.Send(Audio("l-1", forFirst));
        harness.Send(Audio("l-2", forSecond));
        harness.Send(Stop("l-1"));
        harness.Send(Stop("l-2"));
        await harness.Stdout.WaitForAsync("l-1", "complete");
        await harness.Stdout.WaitForAsync("l-2", "complete");

        refused.GetProperty("text").GetString().ShouldBe("Scout could not read that request.");
        live.FedTo(1).ShouldBe([forFirst], "the unknown stop ended neither open session");
        live.FedTo(2).ShouldBe([forSecond], "the unknown stop ended neither open session");
    }

    [Theory]
    [InlineData("audio")]
    [InlineData("stop")]
    public async Task Audio_Or_Stop_For_An_Id_That_Is_Not_Listening_Gets_The_Malformed_Fail(string type)
    {
        var live = new FakeLive();
        await using var harness = new LocalChatAdapterTests.Harness(NoAsk, live: live);

        harness.Send(Listen("l-1"));
        harness.Send(type == "audio" ? Audio("l-9", [1, 2]) : Stop("l-9"));
        var refused = await harness.Stdout.WaitForAsync("l-9", "fail");

        refused.GetProperty("text").GetString().ShouldBe("Scout could not read that request.");
        live.Fed.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_Feed_That_Throws_Ends_The_Session_With_The_Fail_Sentence_And_A_New_Listen_Is_Accepted()
    {
        var live = new FakeLive { FeedFails = true };
        await using var harness = new LocalChatAdapterTests.Harness(NoAsk, live: live);

        harness.Send(Listen("l-1"));
        harness.Send(Audio("l-1", new byte[640]));
        var failed = await harness.Stdout.WaitForAsync("l-1", "fail");
        harness.Send(Listen("l-2"));
        harness.Send(Stop("l-2"));
        await harness.Stdout.WaitForAsync("l-2", "complete");

        failed.GetProperty("text").GetString().ShouldBe("Scout could not transcribe that; see Scout logs.");
        harness.Stdout.KindsFor("l-1").ShouldBe(["fail"], "the failed session ends with its fail and never completes");
        harness.Stdout.KindsFor("l-2").ShouldBe(["complete"], "the slot was freed, so the next listen is accepted");
        live.Starts.ShouldBe(2);
    }

    [Fact]
    public async Task A_Session_That_Fails_To_Transcribe_Ends_With_A_Fail_Event_And_No_Complete()
    {
        var logger = new RecordingLogger<LocalChatAdapter>();
        var live = new FakeLive { StopFails = true };
        await using var harness = new LocalChatAdapterTests.Harness(NoAsk, live: live, logger: logger);

        harness.Send(Listen("l-1"));
        harness.Send(Stop("l-1"));
        var failed = await harness.Stdout.WaitForAsync("l-1", "fail");

        failed.GetProperty("text").GetString().ShouldBe("Scout could not transcribe that; see Scout logs.");
        harness.Stdout.KindsFor("l-1").ShouldNotContain("complete");
        lock (logger.Entries)
        {
            logger.Entries.ShouldContain(e => e.Message.Contains("l-1") && e.Level == Microsoft.Extensions.Logging.LogLevel.Error);
        }
    }

    [Fact]
    public async Task Two_listen_sessions_run_at_once_and_each_final_carries_its_own_id()
    {
        var live = new FakeLive { PartialOnFeed = "heard", FinalOnStop = "said", TextsNameTheSession = true };
        await using var harness = new LocalChatAdapterTests.Harness(NoAsk, live: live);

        harness.Send(Listen("l-1"));
        harness.Send(Listen("l-2"));
        harness.Send(Audio("l-1", new byte[640]));
        harness.Send(Audio("l-2", new byte[640]));
        harness.Send(Stop("l-2"));
        harness.Send(Stop("l-1"));
        await harness.Stdout.WaitForAsync("l-1", "complete");
        await harness.Stdout.WaitForAsync("l-2", "complete");

        harness.Stdout.KindsFor("l-1").ShouldBe(["partial", "final", "complete"]);
        harness.Stdout.KindsFor("l-2").ShouldBe(["partial", "final", "complete"]);
        (await harness.Stdout.WaitForAsync("l-1", "final")).GetProperty("text").GetString()
            .ShouldBe("said in session 1", "l-1 opened the first session and its final carries l-1");
        (await harness.Stdout.WaitForAsync("l-2", "final")).GetProperty("text").GetString()
            .ShouldBe("said in session 2", "l-2 opened the second session and its final carries l-2");
        live.Starts.ShouldBe(2, "both listens opened a session");
    }

    [Fact]
    public async Task A_Read_While_A_Listen_And_An_Ask_Are_Open_Answers_Its_Text()
    {
        var live = new FakeLive { FinalOnStop = "what did they ask me?" };
        var askRunning = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reader = new LocalChatAdapterTests.FakeReader(_ => "Deploy at 14:00");
        await using var harness = new LocalChatAdapterTests.Harness(
            async (_, sink, ct) =>
            {
                await sink.BeginAsync(ct);
                askRunning.TrySetResult();
                await release.Task.WaitAsync(ct);
                await sink.CompleteAsync("answered", [], ct);
            },
            live: live,
            reader: reader);

        harness.Send(Listen("l-1"));
        harness.Send(JsonSerializer.Serialize(new { type = "ask", id = "q-1", text = "when is the deploy?" }));
        await askRunning.Task.WaitAsync(TimeSpan.FromSeconds(10));
        harness.Send(LocalChatAdapterTests.Read("r-1", [0xFF, 0xD8, 0xFF, 0xD9]));
        var read = await harness.Stdout.WaitForAsync("r-1", "text");
        var askWhileOpen = harness.Stdout.KindsFor("q-1");
        var listenWhileOpen = harness.Stdout.KindsFor("l-1");
        release.SetResult();
        harness.Send(Stop("l-1"));
        await harness.Stdout.WaitForAsync("q-1", "complete");
        await harness.Stdout.WaitForAsync("l-1", "complete");

        read.GetProperty("text").GetString().ShouldBe("Deploy at 14:00");
        askWhileOpen.ShouldBe(["begin"], "the read is answered while the ask still runs");
        listenWhileOpen.ShouldBeEmpty("the read is answered while the listen is still open");
        harness.Stdout.KindsFor("l-1").ShouldBe(["final", "complete"]);
        harness.Stdout.KindsFor("q-1").ShouldBe(["begin", "complete"]);
    }

    [Fact]
    public async Task A_Keep_While_A_Listen_Is_Open_Is_Answered_And_The_Listen_Goes_On()
    {
        var live = new FakeLive { FinalOnFeed = n => $"line {n}" };
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new LocalChatAdapterTests.FakeMeetingStore { HoldKeep = hold };
        await using var harness = new LocalChatAdapterTests.Harness(NoAsk, live: live, meetings: store);

        harness.Send(Listen("l-1"));
        harness.Send(LocalChatAdapterTests.Keep("k-1"));
        harness.Send(Audio("l-1", [0x01, 0x02]));
        var finalWhileKeeping = await harness.Stdout.WaitForAsync("l-1", "final");
        var keepWhileHeld = harness.Stdout.KindsFor("k-1");
        hold.SetResult();
        var kept = await harness.Stdout.WaitForAsync("k-1", "kept");
        harness.Send(Audio("l-1", [0x03]));
        await harness.Stdout.WaitForAsync("l-1", "final", Seq(2));
        harness.Send(Stop("l-1"));
        await harness.Stdout.WaitForAsync("l-1", "complete");

        finalWhileKeeping.GetProperty("text").GetString().ShouldBe("line 1");
        keepWhileHeld.ShouldBeEmpty("the store was still saving when the listen's final was written");
        kept.GetProperty("meetingId").GetString().ShouldBe(store.Kept.ShouldHaveSingleItem().Id);
        harness.Stdout.KindsFor("l-1").ShouldBe(["final", "final", "complete"]);
    }

    [Fact]
    public async Task A_Listen_With_Translate_Writes_A_Translation_Event_For_Each_Final_With_Its_Seq()
    {
        var live = new FakeLive { FinalOnFeed = n => $"linha {n}", FinalOnStop = "fim" };
        var translator = new GatedTranslator { Answering = c => Task.FromResult($"{c.Language}: {c.Text}") };
        await using var harness = new LocalChatAdapterTests.Harness(NoAsk, live: live, translator: translator);

        harness.Send(Listen("l-1", "es"));
        harness.Send(Audio("l-1", new byte[640]));
        await harness.Stdout.WaitForAsync("l-1", "translation", Seq(1));
        harness.Send(Audio("l-1", new byte[640]));
        await harness.Stdout.WaitForAsync("l-1", "translation", Seq(2));
        harness.Send(Stop("l-1"));
        await harness.Stdout.WaitForAsync("l-1", "complete");

        EventsOf(harness, "l-1", "final").Select(e => (e.GetProperty("seq").GetInt32(), e.GetProperty("text").GetString()))
            .ShouldBe([(1, "linha 1"), (2, "linha 2"), (3, "fim")], "each final carries its 1-based seq");
        EventsOf(harness, "l-1", "translation").Select(Translation).ShouldBe(
        [
            (1, "es", "es: linha 1", null),
            (2, "es", "es: linha 2", null),
            (3, "es", "es: fim", null),
        ], "each final is translated into the asked language under its own seq");
        harness.Stdout.KindsFor("l-1").ShouldBe(["final", "translation", "final", "translation", "final", "translation", "complete"]);
    }

    [Theory]
    [InlineData(true, "pt-BR", false, "fr-FR", "pt-BR")]
    [InlineData(true, "  ", false, "fr-FR", "fr-FR")]
    [InlineData(true, null, false, "fr-FR", "fr-FR")]
    [InlineData(true, null, true, "fr-FR", "fr-FR")]
    [InlineData(false, null, false, "fr-FR", "fr-FR")]
    [InlineData(false, null, false, "", "en")]
    public async Task Auto_Resolves_To_The_Mandate_Language_When_Known_Else_The_UI_Culture(
        bool mandateWired, string? mandate, bool mandateFails, string uiCulture, string expected)
    {
        var culture = CultureInfo.CurrentUICulture;
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(uiCulture);
        try
        {
            Func<CancellationToken, Task<string?>>? mandateLanguage = !mandateWired
                ? null
                : mandateFails
                    ? _ => Task.FromException<string?>(new InvalidOperationException("Scout could not reach its control plane."))
                    : _ => Task.FromResult(mandate);
            var live = new FakeLive { FinalOnStop = "bom dia" };
            var translator = new GatedTranslator { Answering = c => Task.FromResult("good morning") };
            await using var harness = new LocalChatAdapterTests.Harness(
                NoAsk, live: live, translator: translator, mandateLanguage: mandateLanguage);

            harness.Send(Listen("l-1", "auto"));
            harness.Send(Stop("l-1"));
            await harness.Stdout.WaitForAsync("l-1", "complete");

            translator.Calls.ShouldHaveSingleItem().Language.ShouldBe(expected);
            Translation(EventsOf(harness, "l-1", "translation").ShouldHaveSingleItem())
                .ShouldBe((1, expected, "good morning", null));
        }
        finally
        {
            CultureInfo.CurrentUICulture = culture;
        }
    }

    [Fact]
    public async Task Translate_Without_An_AI_Source_Writes_One_Note_And_Transcribes_On()
    {
        var live = new FakeLive { FinalOnFeed = n => $"linha {n}" };
        await using var harness = new LocalChatAdapterTests.Harness(NoAsk, live: live);

        harness.Send(Listen("l-1", "es"));
        harness.Send(Audio("l-1", new byte[640]));
        harness.Send(Audio("l-1", new byte[640]));
        harness.Send(Stop("l-1"));
        await harness.Stdout.WaitForAsync("l-1", "complete");

        Translation(EventsOf(harness, "l-1", "translation").ShouldHaveSingleItem())
            .ShouldBe((0, "es", null, "Scout has no AI Source for translation; connect one in AI Sources."));
        EventsOf(harness, "l-1", "final").Select(e => e.GetProperty("text").GetString())
            .ShouldBe(["linha 1", "linha 2"], "the listen is accepted and transcription continues untranslated");
        harness.Stdout.KindsFor("l-1").ShouldNotContain("fail");
        harness.Stdout.KindsFor("l-1")[^1].ShouldBe("complete");
    }

    [Fact]
    public async Task Complete_Is_Written_After_The_Last_Translation()
    {
        var live = new FakeLive { FinalOnStop = "fim" };
        var translator = new GatedTranslator();
        await using var harness = new LocalChatAdapterTests.Harness(NoAsk, live: live, translator: translator);

        harness.Send(Listen("l-1", "es"));
        harness.Send(Stop("l-1"));
        var last = await translator.NextCallAsync();
        await harness.Stdout.WaitForAsync("l-1", "final");
        var whileTranslating = harness.Stdout.KindsFor("l-1");
        last.Answer("the end");
        await harness.Stdout.WaitForAsync("l-1", "complete");

        whileTranslating.ShouldBe(["final"], "complete waits while the last final is being translated");
        harness.Stdout.KindsFor("l-1").ShouldBe(["final", "translation", "complete"]);
        Translation(EventsOf(harness, "l-1", "translation").ShouldHaveSingleItem()).ShouldBe((1, "es", "the end", null));
    }

    [Theory]
    [InlineData("feed")]
    [InlineData("stop")]
    public async Task A_Failed_Feed_Or_Stop_Drains_The_Translation_Before_Its_Fail_And_Nothing_Follows(string failing)
    {
        var live = new FakeLive
        {
            FinalOnFeed = n => $"linha {n}",
            FeedFailsOnFeed = failing == "feed" ? 2 : null,
            StopFails = failing == "stop",
        };
        var translator = new GatedTranslator();
        await using var harness = new LocalChatAdapterTests.Harness(NoAsk, live: live, translator: translator);

        harness.Send(Listen("l-1", "es"));
        harness.Send(Audio("l-1", new byte[640]));
        var held = await translator.NextCallAsync();
        harness.Send(failing == "feed" ? Audio("l-1", new byte[640]) : Stop("l-1"));
        await harness.Stdout.WaitForAsync("l-1", "fail");
        var atFail = harness.Stdout.KindsFor("l-1");
        held.Answer("line 1");
        await Task.Delay(TimeSpan.FromMilliseconds(200));

        atFail.ShouldBe(["final", "translation", "fail"], "the line in flight is reported before the fail");
        Translation(EventsOf(harness, "l-1", "translation").ShouldHaveSingleItem()).ShouldBe((1, "es", null, "skipped"));
        held.Token.IsCancellationRequested.ShouldBeTrue("the failure cancels the translation in flight at once");
        harness.Stdout.KindsFor("l-1").ShouldBe(["final", "translation", "fail"], "releasing the translation afterwards writes nothing");
        translator.Calls.Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_Translator_That_Throws_Never_Ends_The_Session()
    {
        var live = new FakeLive { FinalOnFeed = n => $"linha {n}", FinalOnStop = "fim" };
        var translator = new GatedTranslator
        {
            Answering = _ => Task.FromException<string>(new HttpRequestException("The connection was reset.")),
        };
        await using var harness = new LocalChatAdapterTests.Harness(NoAsk, live: live, translator: translator);
        byte[] second = [0x02, 0x02];

        harness.Send(Listen("l-1", "es"));
        harness.Send(Audio("l-1", new byte[640]));
        await harness.Stdout.WaitForAsync("l-1", "translation", Seq(1));
        harness.Send(Audio("l-1", second));
        await harness.Stdout.WaitForAsync("l-1", "translation", Seq(2));
        harness.Send(Stop("l-1"));
        await harness.Stdout.WaitForAsync("l-1", "complete");

        EventsOf(harness, "l-1", "translation").Select(Translation).ShouldBe(
        [
            (1, "es", null, "Scout could not translate that line; see Scout logs."),
            (2, "es", null, "Scout could not translate that line; see Scout logs."),
            (3, "es", null, "Scout could not translate that line; see Scout logs."),
        ]);
        live.Fed[^1].ShouldBe(second, "audio after a failed translation still reaches the session");
        harness.Stdout.KindsFor("l-1").ShouldNotContain("fail", "a failed translation never ends the session");
        harness.Stdout.KindsFor("l-1")[^1].ShouldBe("complete");
    }

    [Fact]
    public async Task Translation_Never_Appears_In_The_Log()
    {
        var logger = new RecordingLogger<LocalChatAdapter>();
        var live = new FakeLive { FinalOnFeed = n => n == 1 ? "the launch code is 4471" : "the vault opens at noon" };
        var translator = new GatedTranslator
        {
            Answering = c => c.Text.Contains("launch")
                ? Task.FromResult("o código de lançamento é 4471")
                : Task.FromException<string>(new InvalidOperationException($"The provider echoed: {c.Text}")),
        };
        await using var harness = new LocalChatAdapterTests.Harness(NoAsk, live: live, logger: logger, translator: translator);

        harness.Send(Listen("l-1", "pt-BR"));
        harness.Send(Audio("l-1", new byte[640]));
        await harness.Stdout.WaitForAsync("l-1", "translation", Seq(1));
        harness.Send(Audio("l-1", new byte[640]));
        await harness.Stdout.WaitForAsync("l-1", "translation", Seq(2));
        harness.Send(Stop("l-1"));
        await harness.Stdout.WaitForAsync("l-1", "complete");

        Translation(await harness.Stdout.WaitForAsync("l-1", "translation", Seq(1))).Text
            .ShouldBe("o código de lançamento é 4471", "the translation does reach stdout");
        lock (logger.Entries)
        {
            logger.Entries.ShouldContain(e => e.Message.Contains("l-1") && e.Message.Contains("pt-BR"), "the translation is logged by id and language");
            logger.Entries.ShouldNotContain(e => e.Message.Contains("4471"));
            logger.Entries.ShouldNotContain(e => e.Message.Contains("código"));
            logger.Entries.ShouldNotContain(e => e.Message.Contains("vault opens"));
        }
    }
}
