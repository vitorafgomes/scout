using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Scout.Conversation;
using Scout.Conversation.Abstractions;
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
            "Voice messages need a Whisper model. Download one in the Scout app under AI Sources, Local Models.");
    }

    [Fact]
    public async Task With_A_Model_File_The_Host_Hands_Voice_Messages_To_The_Local_Transcriber()
    {
        using var host = Build(_fakeModel);

        (await ReplyToVoiceAsync(host)).ShouldBe(
            "Scout could not transcribe that voice message. Try again or type the question.");
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
