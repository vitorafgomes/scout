#pragma warning disable SKEXP0001
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.TextToAudio;
using Scout.Operator.Tests.Fakes;

namespace Scout.Operator.Tests;

public sealed class NarrationGeneratorTests : IDisposable
{
    private readonly string _media = Directory.CreateTempSubdirectory("scout-narration-").FullName;
    private readonly string _bundle = Directory.CreateTempSubdirectory("scout-narration-voices-").FullName;
    private readonly FakeProcessRunner _runner = new();

    public void Dispose()
    {
        Directory.Delete(_media, recursive: true);
        Directory.Delete(_bundle, recursive: true);
    }

    private static NarrationGenerator Generator(Kernel kernel) => new(kernel, Given.Capabilities(kernel), Given.Piper(new FakeProcessRunner()));

    private NarrationGenerator WithVoices(Kernel kernel)
    {
        var (piper, voices) = Given.BundledVoices(_bundle);
        var capabilities = Given.Capabilities(kernel) with
        {
            SpeechVoices = ["alloy", "nova"],
            SpeechSettingsFor = voice => new PromptExecutionSettings { ModelId = $"voice-{voice}" },
        };
        return new(kernel, capabilities, Given.Piper(_runner, piper, voices));
    }

    private string Model(string stem) => Path.Combine(_bundle, "voices", $"{stem}.onnx");

    [Fact]
    public async Task Uses_provider_voice_from_mandate()
    {
        var speech = Given.Speech([7, 8, 9]);

        var result = await WithVoices(Given.Media(speech: speech)).GenerateAsync(Given.Scene("s1", 1), "Test:nova", _media, CancellationToken.None);

        result.ShouldBe(new NarrationResult("generated", "scene-s1.mp3", 1600, null));
        File.ReadAllBytes(Path.Combine(_media, "scene-s1.mp3")).ShouldBe(new byte[] { 7, 8, 9 });
        await speech.Received(1).GetAudioContentsAsync(
            "Feed the starter daily.",
            Arg.Is<PromptExecutionSettings?>(s => s!.ModelId == "voice-nova"),
            Arg.Any<Kernel?>(),
            Arg.Any<CancellationToken>());
        _runner.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_Bundled_Voice_From_The_Mandate_Is_Used_Even_With_A_Provider_Speech_Service()
    {
        var speech = Given.Speech();

        var result = await WithVoices(Given.Media(speech: speech))
            .GenerateAsync(Given.Scene("s2", 2), " piper:en_GB-aru-medium#1 ", _media, CancellationToken.None);

        result.ShouldBe(new NarrationResult("generated", "scene-s2.wav", 1600, null));
        var arguments = _runner.Calls.ShouldHaveSingleItem().Arguments;
        (arguments[1], arguments[^2], arguments[^1]).ShouldBe((Model("en_GB-aru-medium"), "--speaker", "1"));
        Directory.GetFiles(_media).ShouldBe([Path.Combine(_media, "scene-s2.wav")]);
        speech.ReceivedCalls().ShouldBeEmpty();
    }

    [Fact]
    public async Task Uses_piper_default_when_provider_has_no_speech()
    {
        var result = await WithVoices(Given.Media()).GenerateAsync(Given.Scene("s1", 1), string.Empty, _media, CancellationToken.None);

        result.ShouldBe(new NarrationResult("generated", "scene-s1.wav", 1600, null));
        var arguments = _runner.Calls.ShouldHaveSingleItem().Arguments;
        (arguments.Count, arguments[1]).ShouldBe((4, Model("en_GB-cori-medium")));
        _runner.Inputs.ShouldBe(["Feed the starter daily."]);
        Directory.GetFiles(_media).ShouldBe([Path.Combine(_media, "scene-s1.wav")]);
    }

    [Fact]
    public async Task Without_A_Chosen_Voice_The_Providers_Speech_Comes_Before_The_Bundled_Voices()
    {
        var speech = Given.Speech();

        var result = await WithVoices(Given.Media(speech: speech)).GenerateAsync(Given.Scene("s1", 1), string.Empty, _media, CancellationToken.None);

        result.AudioFile.ShouldBe("scene-s1.mp3");
        await speech.Received(1).GetAudioContentsAsync(
            "Feed the starter daily.",
            Arg.Is<PromptExecutionSettings?>(s => s!.ModelId == "speech-settings"),
            Arg.Any<Kernel?>(),
            Arg.Any<CancellationToken>());
        _runner.Calls.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("OpenAI:nova", true)]
    [InlineData("Test:verse", true)]
    [InlineData("nova", true)]
    [InlineData("Test:nova", false)]
    [InlineData("piper:en_GB-alan-medium", true)]
    public async Task Unknown_voice_fails_scene(string voice, bool withSpeech)
    {
        var speech = Given.Speech();

        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => WithVoices(withSpeech ? Given.Media(speech: speech) : Given.Media())
                .GenerateAsync(Given.Scene("s1", 1), voice, _media, CancellationToken.None));

        ex.Message.ShouldBe($"The narration voice '{voice}' is not available on this machine; choose another in Scout Desktop, Production.");
        speech.ReceivedCalls().ShouldBeEmpty();
        _runner.Calls.ShouldBeEmpty();
        Directory.GetFiles(_media).ShouldBeEmpty();
    }

    [Fact]
    public async Task With_A_Speech_Service_The_Narration_Is_Saved_As_Scene_Id_Mp3_In_The_Media_Directory_With_The_Speech_Settings()
    {
        var speech = Given.Speech([7, 8, 9]);
        var kernel = Given.Media(speech: speech);

        var result = await Generator(kernel).GenerateAsync(
            Given.Scene("s2", 2, "one two three four five six seven eight nine ten"), string.Empty, _media, CancellationToken.None);

        result.ShouldBe(new NarrationResult("generated", "scene-s2.mp3", 4000, null));
        File.ReadAllBytes(Path.Combine(_media, "scene-s2.mp3")).ShouldBe(new byte[] { 7, 8, 9 });
        Directory.GetFiles(_media).ShouldBe([Path.Combine(_media, "scene-s2.mp3")]);
        await speech.Received(1).GetAudioContentsAsync(
            "one two three four five six seven eight nine ten",
            Arg.Is<PromptExecutionSettings?>(s => s!.ModelId == "speech-settings"),
            Arg.Any<Kernel?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Without_A_Speech_Service_The_Scene_Has_No_Audio_A_Duration_At_150_Words_A_Minute_And_A_Note()
    {
        var narration = string.Join(' ', Enumerable.Repeat("word", 300));

        var result = await Generator(Given.Media()).GenerateAsync(Given.Scene("s1", 1, narration), string.Empty, _media, CancellationToken.None);

        result.ShouldBe(new NarrationResult(
            "none",
            null,
            120_000,
            "No narration: the connected AI Source has no speech service in Scout; attach your own audio in Scout Desktop, Production."));
        Directory.GetFiles(_media).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_Speech_Service_That_Throws_Fails_The_Scene_With_A_Readable_Reason_And_Writes_Nothing()
    {
        var speech = Substitute.For<ITextToAudioService>();
        speech.GetAudioContentsAsync(Arg.Any<string>(), Arg.Any<PromptExecutionSettings?>(), Arg.Any<Kernel?>(), Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<AudioContent>>(_ => throw new HttpRequestException("503 Service Unavailable"));

        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => Generator(Given.Media(speech: speech)).GenerateAsync(Given.Scene("s3", 3), string.Empty, _media, CancellationToken.None));

        ex.Message.ShouldBe("The AI Source could not make the narration of scene 3; regenerate it in Scout Desktop, Production.");
        Directory.GetFiles(_media).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_Media_Directory_Scout_Cannot_Write_Fails_The_Scene_Saying_The_File_Could_Not_Be_Saved()
    {
        var blocked = Path.Combine(_media, "not-a-directory");
        File.WriteAllText(blocked, "a file where the media directory should be");

        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => Generator(Given.Media(speech: Given.Speech())).GenerateAsync(Given.Scene("s1", 1), string.Empty, blocked, CancellationToken.None));

        ex.Message.ShouldBe("Scout could not save scene-s1.mp3 in the project's media folder.");
    }
}
