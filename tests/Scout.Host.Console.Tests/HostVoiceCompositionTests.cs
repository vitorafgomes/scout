using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Scout.Conversation;
using Scout.Conversation.Abstractions;
using Scout.Transcription.Whisper;
using Shouldly;
using Xunit;

namespace Scout.Host.Console.Tests;

public sealed class HostVoiceCompositionTests : IDisposable
{
    private readonly string _fakeModel = Path.Combine(Path.GetTempPath(), $"scout-host-fake-ggml-{Guid.NewGuid():N}.bin");

    public HostVoiceCompositionTests() => File.WriteAllBytes(_fakeModel, new byte[512]);

    public void Dispose() => File.Delete(_fakeModel);

    private static IHost Build(string? modelPath)
        => new HostBuilder()
            .ConfigureServices((_, services) =>
            {
                services.AddLogging();
                services.AddScoutHost(new ConfigurationBuilder()
                    .AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["Scout:Llm:Provider"] = "None",
                        ["Scout:Transcription:ModelPath"] = modelPath,
                    })
                    .Build());
            })
            .Build();

    private static async Task<string?> ReplyToVoiceAsync(IHost host)
    {
        var conductor = host.Services.GetRequiredService<ConversationConductor>();
        var sink = new FinalTextSink();
        var audio = new AudioAttachment(
            "audio/ogg", 16, TimeSpan.FromSeconds(2), _ => Task.FromResult<Stream>(new MemoryStream(new byte[16])));

        await conductor.HandleAsync(new ChatMessage("u1", "c1", string.Empty, null, audio), sink, CancellationToken.None);
        return sink.FinalText;
    }

    [Fact]
    public async Task Without_A_Model_The_Host_Answers_A_Voice_Message_With_The_Download_Help()
    {
        using var host = Build(modelPath: null);

        (await ReplyToVoiceAsync(host)).ShouldBe(
            "Voice messages need a Whisper model. Download one in the Scout app under Models, Transcription.");
    }

    [Fact]
    public async Task With_A_Model_File_The_Host_Hands_Voice_Messages_To_The_Local_Transcriber()
    {
        using var host = Build(_fakeModel);

        (await ReplyToVoiceAsync(host)).ShouldBe(
            "Scout could not transcribe that voice message. Try again or type the question.");
    }

    [Fact]
    public void With_A_Model_Path_The_Host_Registers_The_Whisper_Transcribers_For_Voice_Messages_And_Live_Listening()
    {
        using var host = Build(_fakeModel);

        host.Services.GetService<IAudioTranscriber>().ShouldBeOfType<WhisperAudioTranscriber>();
        host.Services.GetService<ILiveTranscriber>()!.GetType().Name.ShouldBe("WhisperLiveTranscriber");
    }

    [Fact]
    public void Without_A_Model_Path_The_Host_Registers_No_Transcriber()
    {
        using var host = Build(modelPath: null);

        host.Services.GetService<IAudioTranscriber>().ShouldBeNull();
        host.Services.GetService<ILiveTranscriber>().ShouldBeNull();
    }

    [Fact]
    public void A_Model_Path_To_A_Deleted_File_Registers_No_Transcriber()
    {
        var deleted = Path.Combine(Path.GetTempPath(), $"scout-host-deleted-ggml-{Guid.NewGuid():N}.bin");

        using var host = Build(deleted);

        host.Services.GetService<IAudioTranscriber>().ShouldBeNull();
        host.Services.GetService<ILiveTranscriber>().ShouldBeNull();
    }

    private sealed class FinalTextSink : IResponseSink
    {
        public string? FinalText { get; private set; }

        public RenderCapabilities Capabilities { get; } = new() { FineGrainedEdit = true, MaxBodyLength = 4096 };

        public Task BeginAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task UpdateAsync(string cumulativeText, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task CompleteAsync(string finalText, IReadOnlyList<Citation> citations, CancellationToken cancellationToken)
        {
            FinalText = finalText;
            return Task.CompletedTask;
        }

        public Task FailAsync(string finalTextSoFar, string reason, CancellationToken cancellationToken)
        {
            FinalText = finalTextSoFar;
            return Task.CompletedTask;
        }
    }
}
