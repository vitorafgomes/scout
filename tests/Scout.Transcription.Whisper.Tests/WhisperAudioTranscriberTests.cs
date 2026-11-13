using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Scout.Conversation.Abstractions;
using Whisper.net;

namespace Scout.Transcription.Whisper.Tests;

public sealed class WhisperAudioTranscriberTests : IDisposable
{
    private readonly string _fakeModel = Path.Combine(Path.GetTempPath(), $"scout-fake-ggml-{Guid.NewGuid():N}.bin");

    public WhisperAudioTranscriberTests() => File.WriteAllBytes(_fakeModel, new byte[1024]);

    public void Dispose() => File.Delete(_fakeModel);

    private static FileStream Fixture() => File.OpenRead(Path.Combine(AppContext.BaseDirectory, "Fixtures", "tone-440hz-1500ms.ogg"));

    [Fact]
    [SuppressMessage("Usage", "CA2201", Justification = "Whisper.net raises a plain System.Exception when its native library does not load; the test raises the same type.")]
    public void A_Speech_Runtime_That_Cannot_Load_Fails_With_A_Sentence_The_Owner_Can_Act_On()
    {
        var loader = new Exception("Failed to load native whisper library. Error: libgomp.so.1: cannot open shared object file: No such file or directory");

        var thrown = Should.Throw<TranscriptionUnavailableException>(
            () => WhisperAudioTranscriber.LoadWhisper("model.bin", _ => throw loader));

        thrown.Message.ShouldBe(
            "The speech runtime did not load on this computer; on Linux install libgomp (Debian, Ubuntu: libgomp1). The loader's error is in Scout's logs.");
        thrown.Message[..120].ShouldContain("on Linux install libgomp (Debian, Ubuntu: libgomp1)", Case.Sensitive, "a recording's failure keeps only the first 120 characters, so the action must sit inside them");
        thrown.InnerException.ShouldBeSameAs(loader, "the loader's own error stays for the logs");
    }

    [Fact]
    public async Task A_File_That_Is_Not_A_Whisper_Model_Fails_As_A_Model_Load_Error_Where_The_Runtime_Loads_And_As_Unavailable_Where_It_Does_Not()
    {
        var runtime = !OperatingSystem.IsLinux() || NativeLibrary.TryLoad("libgomp.so.1", out _);
        if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true")
        {
            runtime.ShouldBeTrue("CI's runners carry libgomp, so CI proves the model-load branch and never drifts into the other");
        }
        using var transcriber = new WhisperAudioTranscriber(_fakeModel);
        await using var ogg = Fixture();

        var thrown = await Should.ThrowAsync<Exception>(
            () => transcriber.TranscribeAsync(ogg, "audio/ogg", CancellationToken.None));

        if (runtime)
        {
            thrown.ShouldBeOfType<WhisperModelLoadException>();
        }
        else
        {
            thrown.ShouldBeOfType<TranscriptionUnavailableException>().Message.ShouldBe(WhisperAudioTranscriber.RuntimeUnavailable);
        }
    }

    [Fact]
    public async Task Audio_That_Is_Not_Ogg_Is_Refused_Before_The_Model_Loads()
    {
        using var transcriber = new WhisperAudioTranscriber(_fakeModel);
        await using var audio = new MemoryStream(new byte[16]);

        await Should.ThrowAsync<NotSupportedException>(
            () => transcriber.TranscribeAsync(audio, "audio/mpeg", CancellationToken.None));
    }

    [Fact]
    public void An_Existing_Model_Path_Registers_The_Transcriber_Without_Loading_The_Model()
    {
        using var provider = Compose(_fakeModel);

        provider.GetService<IAudioTranscriber>().ShouldBeOfType<WhisperAudioTranscriber>();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("/nonexistent/scout-test/ggml-large-v3-turbo.bin")]
    public void Without_A_Model_File_No_Transcriber_Is_Registered(string? modelPath)
    {
        using var provider = Compose(modelPath);

        provider.GetService<IAudioTranscriber>().ShouldBeNull();
    }

    private static ServiceProvider Compose(string? modelPath)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Scout:Transcription:ModelPath"] = modelPath })
            .Build();
        return new ServiceCollection().AddWhisperTranscription(configuration).BuildServiceProvider();
    }
}
