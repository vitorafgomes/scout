using System.Text.Json;
using Scout.Conversation.Abstractions;
using static Scout.Adapter.Local.Tests.LocalChatAdapterTests;

namespace Scout.Adapter.Local.Tests;

public sealed class LocalSpeakTests
{
    private const string Sentence = "The build on main is green.";

    private const string NoVoice =
        "No voice: connect an AI Source with speech (OpenAI or Azure OpenAI), or use a Scout Desktop package, which carries Scout's local voice.";

    private static readonly Func<ChatMessage, IResponseSink, CancellationToken, Task> NeverAsked = (_, _, _) => throw new InvalidOperationException("never asked");

    private static readonly SpeechVoices AiSourceAndLocal = new(
        "OpenAI",
        ["alloy", "nova"],
        [new SpeechVoice("piper:en_GB-cori-medium", "Cori (medium)"), new SpeechVoice("piper:en_GB-aru-medium#0", "Aru 03 (medium)")],
        "piper:en_GB-cori-medium");

    private static readonly SpeechVoices LocalVoicesOnly = AiSourceAndLocal with { Provider = "Ollama", ProviderVoices = [] };

    private sealed class FakeSpeech(Func<string, SpeechAudio?> answer, SpeechVoices? voices = null, Exception? listing = null) : ISpeechSynthesizer
    {
        private readonly List<string> _texts = [];
        private readonly List<SpeechChoice> _choices = [];

        public IReadOnlyList<SpeechChoice> Choices
        {
            get
            {
                lock (_texts)
                {
                    return [.. _choices];
                }
            }
        }

        public IReadOnlyList<string> Texts
        {
            get
            {
                lock (_texts)
                {
                    return [.. _texts];
                }
            }
        }

        public Task<SpeechAudio?> SynthesizeAsync(string text, SpeechChoice choice, CancellationToken ct)
        {
            lock (_texts)
            {
                _texts.Add(text);
                _choices.Add(choice);
            }
            return Task.FromResult(answer(text));
        }

        public Task<SpeechVoices> VoicesAsync(CancellationToken ct)
            => listing is null ? Task.FromResult(voices ?? AiSourceAndLocal) : Task.FromException<SpeechVoices>(listing);
    }

    private static string Speak(string id, string? text) => JsonSerializer.Serialize(new { type = "speak", id, text });

    private static string Speak(string id, string? providerVoice, string? localVoice, bool localOnly = false)
        => JsonSerializer.Serialize(new { type = "speak", id, text = Sentence, providerVoice, localVoice, localOnly });

    [Theory]
    [InlineData("audio/mpeg")]
    [InlineData("audio/wav")]
    public async Task A_Speak_Request_Answers_One_Audio_Event_With_The_Voices_Bytes_As_Base64(string contentType)
    {
        byte[] bytes = [1, 2, 3, 250];
        var speech = new FakeSpeech(_ => new SpeechAudio(contentType, bytes));
        await using var harness = new Harness(NeverAsked, speech: speech);

        harness.Send(Speak("s-1", Sentence));
        var audio = await harness.Stdout.WaitForAsync("s-1", "audio");

        audio.GetProperty("contentType").GetString().ShouldBe(contentType);
        audio.GetProperty("base64").GetBytesFromBase64().ShouldBe(bytes);
        speech.Texts.ShouldBe([Sentence]);
        harness.Stdout.KindsFor("s-1").ShouldBe(["audio"]);
    }

    [Fact]
    public async Task A_Speak_With_No_Voice_On_This_Machine_Answers_The_No_Voice_Reason()
    {
        await using var harness = new Harness(NeverAsked, speech: new FakeSpeech(_ => null));

        harness.Send(Speak("s-1", Sentence));
        var refused = await harness.Stdout.WaitForAsync("s-1", "fail");

        refused.GetProperty("text").GetString().ShouldBe(NoVoice);
    }

    [Fact]
    public async Task A_Speak_Without_A_Composed_Voice_Answers_The_No_Voice_Reason()
    {
        await using var harness = new Harness(NeverAsked);

        harness.Send(Speak("s-1", Sentence));
        var refused = await harness.Stdout.WaitForAsync("s-1", "fail");

        refused.GetProperty("text").GetString().ShouldBe(NoVoice);
    }

    [Fact]
    public async Task A_Voice_That_Fails_Answers_Its_Reason_And_The_Next_Request_Is_Still_Answered()
    {
        var calls = 0;
        var speech = new FakeSpeech(_ => Interlocked.Increment(ref calls) == 1
            ? throw new InvalidOperationException("The AI Source could not read that aloud; see Scout logs.")
            : new SpeechAudio("audio/wav", [9]));
        await using var harness = new Harness(NeverAsked, speech: speech);

        harness.Send(Speak("s-1", Sentence));
        var failed = await harness.Stdout.WaitForAsync("s-1", "fail");
        harness.Send(Speak("s-2", Sentence));
        await harness.Stdout.WaitForAsync("s-2", "audio");

        failed.GetProperty("text").GetString().ShouldBe("The AI Source could not read that aloud; see Scout logs.");
        harness.Stdout.KindsFor("s-1").ShouldBe(["fail"]);
    }

    [Fact]
    public async Task A_Voice_That_Throws_Anything_Else_Answers_The_Could_Not_Read_Aloud_Sentence()
    {
        await using var harness = new Harness(NeverAsked, speech: new FakeSpeech(_ => throw new IOException("broken pipe")));

        harness.Send(Speak("s-1", Sentence));
        var failed = await harness.Stdout.WaitForAsync("s-1", "fail");

        failed.GetProperty("text").GetString().ShouldBe("Scout could not read that aloud; see Scout logs.");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("over")]
    public async Task A_Speak_With_No_Text_Or_More_Than_2000_Characters_Is_Refused_Before_The_Voice(string? text)
    {
        var speech = new FakeSpeech(_ => new SpeechAudio("audio/wav", [1]));
        await using var harness = new Harness(NeverAsked, speech: speech);

        harness.Send(Speak("s-1", text == "over" ? new string('a', 2001) : text));
        var refused = await harness.Stdout.WaitForAsync("s-1", "fail");

        refused.GetProperty("text").GetString().ShouldBe("Scout could not read that request.");
        speech.Texts.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_Speak_Never_Logs_The_Sentence()
    {
        var logger = new RecordingLogger<LocalChatAdapter>();
        await using var harness = new Harness(NeverAsked, logger: logger, speech: new FakeSpeech(_ => new SpeechAudio("audio/wav", [1, 2])));

        harness.Send(Speak("s-1", Sentence));
        await harness.Stdout.WaitForAsync("s-1", "audio");
        await harness.DisposeAsync();

        logger.Entries.ShouldContain(e => e.Message.Contains("s-1", StringComparison.Ordinal) && e.Message.Contains("bytes=2", StringComparison.Ordinal));
        logger.Entries.ShouldNotContain(e => e.Message.Contains("build on main", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_Speak_Reads_With_The_Provider_Voice_And_Local_Voice_The_Owner_Chose()
    {
        var speech = new FakeSpeech(_ => new SpeechAudio("audio/mpeg", [1]));
        await using var harness = new Harness(NeverAsked, speech: speech);

        harness.Send(Speak("s-1", "nova", "piper:en_GB-aru-medium#0"));
        await harness.Stdout.WaitForAsync("s-1", "audio");

        speech.Choices.ShouldBe([new SpeechChoice("nova", "piper:en_GB-aru-medium#0")]);
    }

    [Fact]
    public async Task A_Sample_Of_A_Local_Voice_Asks_For_The_Local_Voice_Even_When_The_AI_Source_Has_Speech()
    {
        var speech = new FakeSpeech(_ => new SpeechAudio("audio/wav", [1]));
        await using var harness = new Harness(NeverAsked, speech: speech);

        harness.Send(Speak("s-1", null, "piper:en_GB-aru-medium#0", localOnly: true));
        await harness.Stdout.WaitForAsync("s-1", "audio");

        speech.Choices.ShouldBe([new SpeechChoice(null, "piper:en_GB-aru-medium#0", LocalOnly: true)]);
    }

    [Theory]
    [InlineData("bogus", null, false, "bogus")]
    [InlineData("nova", "piper:en_GB-gone-medium", true, "piper:en_GB-gone-medium")]
    public async Task A_Speak_Naming_A_Voice_That_Is_Not_Available_Is_Refused_With_A_Readable_Reason_And_Never_Reaches_The_Voice(
        string? providerVoice, string? localVoice, bool localOnly, string unknown)
    {
        var speech = new FakeSpeech(_ => new SpeechAudio("audio/mpeg", [1]));
        await using var harness = new Harness(NeverAsked, speech: speech);

        harness.Send(Speak("s-1", providerVoice, localVoice, localOnly));
        var refused = await harness.Stdout.WaitForAsync("s-1", "fail");

        refused.GetProperty("text").GetString().ShouldBe(
            $"The voice '{unknown}' is not available on this machine; choose another in Scout Desktop, Models, Speech.");
        speech.Texts.ShouldBeEmpty();
    }

    [Fact]
    public async Task An_Unknown_Local_Voice_Is_Refused_When_The_AI_Source_Has_No_Speech()
    {
        var speech = new FakeSpeech(_ => new SpeechAudio("audio/wav", [1]), LocalVoicesOnly);
        await using var harness = new Harness(NeverAsked, speech: speech);

        harness.Send(Speak("s-1", "nova", "piper:en_GB-gone-medium"));
        var refused = await harness.Stdout.WaitForAsync("s-1", "fail");

        refused.GetProperty("text").GetString().ShouldBe(
            "The voice 'piper:en_GB-gone-medium' is not available on this machine; choose another in Scout Desktop, Models, Speech.");
        speech.Texts.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_Saved_Provider_Voice_Does_Not_Stop_Reading_Aloud_After_Switching_To_An_AI_Source_Without_Speech()
    {
        var speech = new FakeSpeech(_ => new SpeechAudio("audio/wav", [1]), LocalVoicesOnly);
        await using var harness = new Harness(NeverAsked, speech: speech);

        harness.Send(Speak("s-1", "nova", "piper:en_GB-cori-medium"));
        await harness.Stdout.WaitForAsync("s-1", "audio");

        speech.Choices.ShouldBe([new SpeechChoice("nova", "piper:en_GB-cori-medium")]);
    }

    [Fact]
    public async Task A_Voices_Request_Lists_The_AI_Sources_Voices_And_Only_The_Local_Voices_This_Machine_Has()
    {
        await using var harness = new Harness(NeverAsked, speech: new FakeSpeech(_ => null));

        harness.Send(JsonSerializer.Serialize(new { type = "voices", id = "l-1" }));
        var listed = await harness.Stdout.WaitForAsync("l-1", "voices");

        listed.GetProperty("provider").GetString().ShouldBe("OpenAI");
        listed.GetProperty("providerVoices").EnumerateArray().Select(v => v.GetString()).ShouldBe(["alloy", "nova"]);
        listed.GetProperty("localVoices").EnumerateArray()
            .Select(v => (v.GetProperty("id").GetString(), v.GetProperty("label").GetString()))
            .ShouldBe([("piper:en_GB-cori-medium", "Cori (medium)"), ("piper:en_GB-aru-medium#0", "Aru 03 (medium)")]);
        listed.GetProperty("defaultLocal").GetString().ShouldBe("piper:en_GB-cori-medium");
    }

    [Fact]
    public async Task A_Voices_Listing_That_Fails_Answers_The_Could_Not_List_Sentence()
    {
        await using var harness = new Harness(NeverAsked, speech: new FakeSpeech(_ => null, listing: new IOException("voices folder unreadable")));

        harness.Send(JsonSerializer.Serialize(new { type = "voices", id = "l-1" }));
        var failed = await harness.Stdout.WaitForAsync("l-1", "fail");

        failed.GetProperty("text").GetString().ShouldBe("Scout could not list its voices; see Scout logs.");
    }

    [Fact]
    public async Task A_Voices_Request_Without_A_Composed_Voice_Answers_The_No_Voice_Reason()
    {
        await using var harness = new Harness(NeverAsked);

        harness.Send(JsonSerializer.Serialize(new { type = "voices", id = "l-1" }));
        var refused = await harness.Stdout.WaitForAsync("l-1", "fail");

        refused.GetProperty("text").GetString().ShouldBe(NoVoice);
    }
}
