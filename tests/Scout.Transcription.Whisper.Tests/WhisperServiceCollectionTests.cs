using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Scout.Conversation.Abstractions;

namespace Scout.Transcription.Whisper.Tests;

public sealed class WhisperServiceCollectionTests : IDisposable
{
    private readonly string _fakeModel = Path.Combine(Path.GetTempPath(), $"scout-fake-ggml-{Guid.NewGuid():N}.bin");

    public WhisperServiceCollectionTests() => File.WriteAllBytes(_fakeModel, new byte[1024]);

    public void Dispose() => File.Delete(_fakeModel);

    private static ServiceProvider Compose(string? modelPath)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Scout:Transcription:ModelPath"] = modelPath })
            .Build();
        return new ServiceCollection().AddWhisperTranscription(configuration).BuildServiceProvider();
    }

    [Fact]
    public void An_Existing_Model_File_Registers_Live_Transcription_Over_The_Voice_Message_Transcriber()
    {
        using var provider = Compose(_fakeModel);

        var live = provider.GetService<ILiveTranscriber>().ShouldBeOfType<WhisperLiveTranscriber>();
        var voice = provider.GetRequiredService<IAudioTranscriber>().ShouldBeOfType<WhisperAudioTranscriber>();
        live.Model.ShouldBeSameAs(voice, "live and voice messages share one loaded model and one decode gate");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("/nonexistent/scout-test/ggml-base.bin")]
    public void Without_A_Model_File_No_Live_Transcriber_Is_Registered(string? modelPath)
    {
        using var provider = Compose(modelPath);

        provider.GetService<ILiveTranscriber>().ShouldBeNull();
    }
}
