using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Scout.Conversation.Abstractions;

namespace Scout.Transcription.Whisper.Tests;

public sealed class WhisperServiceCollectionTests : IDisposable
{
    private readonly string _fakeModel = Path.Combine(Path.GetTempPath(), $"scout-fake-ggml-{Guid.NewGuid():N}.bin");

    public WhisperServiceCollectionTests() => File.WriteAllBytes(_fakeModel, new byte[1024]);

    public void Dispose() => File.Delete(_fakeModel);

    private static ServiceProvider Compose(string? modelPath, ILogger<WhisperLiveTranscriber>? logger = null)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Scout:Transcription:ModelPath"] = modelPath })
            .Build();
        var services = new ServiceCollection();
        if (logger is not null)
        {
            services.AddSingleton(logger);
        }
        return services.AddWhisperTranscription(configuration).BuildServiceProvider();
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

    private sealed class WorkerLog : ILogger<WhisperLiveTranscriber>
    {
        private readonly Lock _gate = new();
        private readonly List<(LogLevel Level, string Message)> _entries = [];

        public IReadOnlyList<(LogLevel Level, string Message)> Entries
        {
            get
            {
                lock (_gate)
                {
                    return [.. _entries];
                }
            }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (_gate)
            {
                _entries.Add((logLevel, formatter(state, exception)));
            }
        }
    }

    [Fact]
    public async Task The_Composed_Live_Transcriber_Writes_A_Turn_End_To_The_Worker_Log()
    {
        var log = new WorkerLog();
        using var provider = Compose(_fakeModel, log);
        var session = provider.GetRequiredService<ILiveTranscriber>().Start(_ => Task.CompletedTask, CancellationToken.None);
        var speech = new byte[8_000 * 2];
        for (var i = 0; i < 8_000; i++)
        {
            BitConverter.GetBytes((short)(16_000 * Math.Sin(2 * Math.PI * 440 * i / 16_000))).CopyTo(speech, i * 2);
        }
        await session.FeedAsync(speech, CancellationToken.None);
        await Should.ThrowAsync<Exception>(() => session.StopAsync(CancellationToken.None));

        log.Entries.ShouldBe(
            [(LogLevel.Information, "Live turn ended (Flush) after 500 ms of audio; decoding its final text.")],
            "the worker's log shows that speech was heard even when its decode fails");
    }
}
