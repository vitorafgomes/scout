#pragma warning disable SKEXP0001
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.TextToAudio;
using Scout.Conversation.Abstractions;
using Scout.Llm.Abstractions;
using Scout.Operator.Speech;
using Scout.Operator.Tests.Fakes;

namespace Scout.Operator.Tests.Speech;

public sealed class ReplySpeechTests : IDisposable
{
    private const string Sentence = "The build on main is green.";

    private readonly string _root = Directory.CreateTempSubdirectory("scout-reply-speech-").FullName;
    private readonly string _speech;
    private readonly FakeProcessRunner _runner = new();

    public ReplySpeechTests() => _speech = Directory.CreateDirectory(Path.Combine(_root, "speech")).FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    public enum Source
    {
        ProviderAndLocal,
        ProviderOnly,
        LocalOnly,
        LocalWithoutFolder,
        Neither,
    }

    private static MediaCapabilities WithVoices(MediaCapabilities media)
        => media with
        {
            SpeechVoices = media.Speech ? ["alloy", "nova"] : [],
            SpeechSettingsFor = media.Speech ? voice => new PromptExecutionSettings { ModelId = $"voice:{voice}" } : null,
        };

    private ReplySpeech Speech(Source source, ITextToAudioService? provider = null)
    {
        var withProvider = source is Source.ProviderAndLocal or Source.ProviderOnly;
        var withLocal = source is Source.ProviderAndLocal or Source.LocalOnly or Source.LocalWithoutFolder;
        var kernel = Given.Media(speech: withProvider ? provider ?? Given.Speech([7, 8, 9]) : null);
        var (piper, voices) = withLocal ? Given.BundledVoices(_root) : (string.Empty, string.Empty);
        return new ReplySpeech(
            kernel,
            WithVoices(Given.Capabilities(kernel)),
            Given.Piper(_runner, piper, voices),
            source == Source.LocalWithoutFolder ? string.Empty : _speech);
    }

    [Theory]
    [InlineData(Source.ProviderAndLocal, "audio/mpeg", 3, false)]
    [InlineData(Source.ProviderOnly, "audio/mpeg", 3, false)]
    [InlineData(Source.LocalOnly, "audio/wav", 2048, true)]
    public async Task The_Voice_Is_The_AI_Sources_Speech_When_It_Has_One_Else_Scouts_Local_Voice(
        Source source, string contentType, int bytes, bool piperRan)
    {
        var audio = await Speech(source).SynthesizeAsync(Sentence, SpeechChoice.Default, CancellationToken.None);

        audio.ShouldNotBeNull();
        audio.ContentType.ShouldBe(contentType);
        audio.Bytes.Length.ShouldBe(bytes);
        (_runner.Calls.Count == 1).ShouldBe(piperRan);
        if (piperRan)
        {
            _runner.Inputs.ShouldBe([Sentence]);
            _runner.Calls[0].Arguments.ShouldContain(Path.Combine(_root, "voices", "en_GB-cori-medium.onnx"));
        }
    }

    [Theory]
    [InlineData(Source.Neither)]
    [InlineData(Source.LocalWithoutFolder)]
    public async Task With_No_Speech_And_No_Usable_Local_Voice_There_Is_No_Voice(Source source)
    {
        var audio = await Speech(source).SynthesizeAsync(Sentence, SpeechChoice.Default, CancellationToken.None);

        audio.ShouldBeNull();
        _runner.Calls.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task The_Composed_Voice_Renders_Only_In_An_Absolute_Speech_Folder_And_Has_No_Local_Voice_For_A_Relative_One(bool absolute)
    {
        var (piper, voices) = Given.BundledVoices(_root);
        var kernel = Given.Media();
        var services = new ServiceCollection()
            .AddSingleton(kernel)
            .AddSingleton(Given.Capabilities(kernel))
            .AddSingleton<IProcessRunner>(_runner);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [ReplySpeechServiceCollectionExtensions.DirectoryKey] = absolute ? _speech : "speech",
                ["Scout:Operator:PiperPath"] = piper,
                ["Scout:Operator:VoicesPath"] = voices,
            })
            .Build();
        using var provider = services.AddReplySpeech(configuration).BuildServiceProvider();

        var audio = await provider.GetRequiredService<ISpeechSynthesizer>().SynthesizeAsync(Sentence, SpeechChoice.Default, CancellationToken.None);

        (audio?.ContentType, _runner.Calls.Count).ShouldBe(absolute ? ("audio/wav", 1) : (null, 0));
    }

    [Fact]
    public async Task The_Local_Voice_Leaves_No_File_Behind_In_Scouts_Speech_Folder()
    {
        await Speech(Source.LocalOnly).SynthesizeAsync(Sentence, SpeechChoice.Default, CancellationToken.None);

        Directory.GetFiles(_speech).ShouldBeEmpty();
        Path.GetDirectoryName(_runner.Calls[0].Arguments[_runner.Calls[0].Arguments.ToList().IndexOf("--output_file") + 1]).ShouldBe(_speech);
    }

    [Fact]
    public async Task The_Local_Voice_Reads_Aloud_When_Scouts_Speech_Folder_Does_Not_Exist_Yet()
    {
        var folder = Path.Combine(_root, "not-yet");
        var (piper, voices) = Given.BundledVoices(_root);
        var kernel = Given.Media();
        var speech = new ReplySpeech(kernel, Given.Capabilities(kernel), Given.Piper(_runner, piper, voices), folder);

        var audio = await speech.SynthesizeAsync(Sentence, SpeechChoice.Default, CancellationToken.None);

        audio.ShouldNotBeNull().ContentType.ShouldBe("audio/wav");
        Directory.GetFiles(folder).ShouldBeEmpty();
    }

    [Fact]
    public async Task An_AI_Source_Speech_Call_That_Fails_Comes_Back_As_A_Reason_The_Owner_Can_Read()
    {
        var provider = Substitute.For<ITextToAudioService>();
        provider.GetAudioContentsAsync(Arg.Any<string>(), Arg.Any<PromptExecutionSettings?>(), Arg.Any<Kernel?>(), Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<AudioContent>>(_ => throw new HttpRequestException("503 Service Unavailable"));

        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => Speech(Source.ProviderAndLocal, provider).SynthesizeAsync(Sentence, SpeechChoice.Default, CancellationToken.None));

        ex.Message.ShouldBe("The AI Source could not read that aloud; see Scout logs.");
        _runner.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_Local_Voice_Run_That_Fails_Comes_Back_As_A_Reason_The_Owner_Can_Read_And_Leaves_No_File()
    {
        var failing = new FakeProcessRunner { Answer = (_, _) => new ProcessResult(1, string.Empty, "Segmentation fault") };
        var (piper, voices) = Given.BundledVoices(_root);
        var kernel = Given.Media();
        var speech = new ReplySpeech(kernel, Given.Capabilities(kernel), Given.Piper(failing, piper, voices), _speech);

        var ex = await Should.ThrowAsync<InvalidOperationException>(() => speech.SynthesizeAsync(Sentence, SpeechChoice.Default, CancellationToken.None));

        ex.Message.ShouldBe("Scout's local voice could not read that aloud; see Scout logs.");
        Directory.GetFiles(_speech).ShouldBeEmpty();
    }

    [Fact]
    public async Task The_AI_Source_Reads_With_The_Voice_The_Owner_Chose()
    {
        var provider = Given.Speech([7, 8, 9]);

        var audio = await Speech(Source.ProviderAndLocal, provider).SynthesizeAsync(Sentence, new SpeechChoice("nova", "piper:en_GB-aru-medium#1"), CancellationToken.None);

        audio.ShouldNotBeNull().ContentType.ShouldBe("audio/mpeg");
        await provider.Received(1).GetAudioContentsAsync(
            Sentence, Arg.Is<PromptExecutionSettings?>(s => s != null && s.ModelId == "voice:nova"), Arg.Any<Kernel?>(), Arg.Any<CancellationToken>());
        _runner.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task The_AI_Source_Reads_With_Its_Default_Voice_When_The_Owner_Chose_None()
    {
        var provider = Given.Speech([7, 8, 9]);

        await Speech(Source.ProviderOnly, provider).SynthesizeAsync(Sentence, SpeechChoice.Default, CancellationToken.None);

        await provider.Received(1).GetAudioContentsAsync(
            Sentence, Arg.Is<PromptExecutionSettings?>(s => s != null && s.ModelId == "speech-settings"), Arg.Any<Kernel?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task An_AI_Source_Voice_That_Is_Not_In_Its_List_Is_Refused_And_Never_Sent_To_The_AI_Source()
    {
        var provider = Given.Speech([7, 8, 9]);

        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => Speech(Source.ProviderAndLocal, provider).SynthesizeAsync(Sentence, new SpeechChoice("bogus"), CancellationToken.None));

        ex.Message.ShouldBe("The voice 'bogus' is not available on this machine; choose another in Scout Desktop, Models, Speech.");
        provider.ReceivedCalls().ShouldBeEmpty();
    }

    [Fact]
    public async Task An_AI_Source_Speech_Call_With_A_Chosen_Voice_That_Fails_Comes_Back_As_A_Reason_The_Owner_Can_Read()
    {
        var provider = Substitute.For<ITextToAudioService>();
        provider.GetAudioContentsAsync(Arg.Any<string>(), Arg.Any<PromptExecutionSettings?>(), Arg.Any<Kernel?>(), Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<AudioContent>>(_ => throw new HttpRequestException("503 Service Unavailable"));

        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => Speech(Source.ProviderAndLocal, provider).SynthesizeAsync(Sentence, new SpeechChoice("nova"), CancellationToken.None));

        ex.Message.ShouldBe("The AI Source could not read that aloud; see Scout logs.");
    }

    [Fact]
    public async Task Scouts_Local_Voice_Reads_With_The_Chosen_Voice_And_Its_Speaker()
    {
        var audio = await Speech(Source.LocalOnly).SynthesizeAsync(Sentence, new SpeechChoice("nova", "piper:en_GB-aru-medium#1"), CancellationToken.None);

        audio.ShouldNotBeNull().ContentType.ShouldBe("audio/wav");
        var arguments = _runner.Calls.ShouldHaveSingleItem().Arguments.ToList();
        arguments[arguments.IndexOf("--model") + 1].ShouldBe(Path.Combine(_root, "voices", "en_GB-aru-medium.onnx"));
        arguments[arguments.IndexOf("--speaker") + 1].ShouldBe("1");
    }

    [Fact]
    public async Task A_Sample_Of_A_Local_Voice_Reads_With_That_Voice_Even_When_The_AI_Source_Has_Speech()
    {
        var provider = Given.Speech([7, 8, 9]);

        var audio = await Speech(Source.ProviderAndLocal, provider)
            .SynthesizeAsync(Sentence, new SpeechChoice(null, "piper:en_GB-aru-medium#0", LocalOnly: true), CancellationToken.None);

        audio.ShouldNotBeNull().ContentType.ShouldBe("audio/wav");
        var arguments = _runner.Calls.ShouldHaveSingleItem().Arguments.ToList();
        arguments[arguments.IndexOf("--speaker") + 1].ShouldBe("0");
        provider.ReceivedCalls().ShouldBeEmpty();
    }

    [Fact]
    public async Task A_Local_Voice_That_This_Machine_Does_Not_Have_Is_Refused_And_Piper_Never_Runs()
    {
        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => Speech(Source.LocalOnly).SynthesizeAsync(Sentence, new SpeechChoice(null, "piper:en_GB-gone-medium"), CancellationToken.None));

        ex.Message.ShouldBe("The voice 'piper:en_GB-gone-medium' is not available on this machine; choose another in Scout Desktop, Models, Speech.");
        _runner.Calls.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(Source.ProviderAndLocal, new[] { "alloy", "nova" }, 3)]
    [InlineData(Source.LocalOnly, new string[0], 3)]
    [InlineData(Source.ProviderOnly, new[] { "alloy", "nova" }, 0)]
    [InlineData(Source.LocalWithoutFolder, new string[0], 0)]
    public async Task The_Voices_Listed_Are_The_AI_Sources_Only_With_Speech_And_The_Local_Voices_This_Machine_Has(
        Source source, string[] providerVoices, int localVoices)
    {
        var voices = await Speech(source).VoicesAsync(CancellationToken.None);

        voices.Provider.ShouldBe("Test");
        voices.ProviderVoices.ShouldBe(providerVoices);
        voices.LocalVoices.Count.ShouldBe(localVoices);
        voices.DefaultLocal.ShouldBe(localVoices > 0 ? "piper:en_GB-cori-medium" : null);
    }

    [Fact]
    public async Task The_AI_Sources_Voices_Are_Not_Listed_When_Its_Speech_Service_Is_Not_On_The_Kernel()
    {
        var (piper, voices) = Given.BundledVoices(_root);
        var kernel = Given.Media();
        var media = Given.Capabilities(kernel) with { Speech = true, SpeechVoices = ["alloy", "nova"] };

        var listed = await new ReplySpeech(kernel, media, Given.Piper(_runner, piper, voices), _speech).VoicesAsync(CancellationToken.None);

        listed.ProviderVoices.ShouldBeEmpty();
        listed.LocalVoices.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task The_Local_Voices_Listed_Carry_Each_Speaker_With_The_Label_The_Owner_Reads()
    {
        var voices = await Speech(Source.LocalOnly).VoicesAsync(CancellationToken.None);

        voices.LocalVoices.ShouldBe([
            new SpeechVoice("piper:en_GB-aru-medium#0", "Aru 03 (medium)"),
            new SpeechVoice("piper:en_GB-aru-medium#1", "Aru 06 (medium)"),
            new SpeechVoice("piper:en_GB-cori-medium", "Cori (medium)"),
        ]);
    }
}
