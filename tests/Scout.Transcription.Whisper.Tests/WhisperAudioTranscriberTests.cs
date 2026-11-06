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
    public async Task A_File_That_Is_Not_A_Whisper_Model_Fails_The_Transcription_With_A_Model_Load_Error()
    {
        using var transcriber = new WhisperAudioTranscriber(_fakeModel);
        await using var ogg = Fixture();

        await Should.ThrowAsync<WhisperModelLoadException>(
            () => transcriber.TranscribeAsync(ogg, "audio/ogg", CancellationToken.None));
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
