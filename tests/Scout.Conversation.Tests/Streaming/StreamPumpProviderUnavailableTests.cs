using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Scout.Conversation.Abstractions;
using Scout.Conversation.Streaming;
using Scout.Conversation.Tests.Fakes;
using Scout.Llm.Abstractions;

namespace Scout.Conversation.Tests.Streaming;

/// <summary>
/// When the provider is unreachable mid-stream, the pump finalizes via Complete
/// with a friendly user-facing message (not Fail) and logs concisely at Warning
/// without attaching the exception (no stack noise). Hermetic - no real
/// transport.
/// </summary>
public sealed class StreamPumpProviderUnavailableTests
{
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(750);
    private const string Cursor = "▌";

    private static readonly StreamPumpOptions DefaultOptionsProbe = new();
    private static readonly string ProviderMessage = DefaultOptionsProbe.ProviderUnavailableMessage;

    private static readonly RenderCapabilities FineGrained = new()
    {
        FineGrainedEdit = true,
        MaxBodyLength = 4096,
    };

    private static FakeTimeProvider Clock() =>
        new(new DateTimeOffset(2026, 5, 28, 0, 0, 0, TimeSpan.Zero));

    private static StreamPump Pump(FakeTimeProvider time, ILogger? logger = null)
        => new(new StreamPumpOptions
        {
            EditInterval = Interval,
            Cursor = Cursor,
            TimeProvider = time,
            Logger = logger,
        });

    [Fact]
    public async Task ProviderUnavailable_MidStream_Finalizes_With_Friendly_Message_And_Partial()
    {
        var time = Clock();
        var sink = new RecordingResponseSink(FineGrained);
        var response = new ChatResponse(
            AsyncSequence.ThenThrow(
                new LlmProviderUnavailableException("LLM provider unavailable (HttpRequestException)."),
                default,
                "par", "tial"));

        await Should.NotThrowAsync(
            () => Pump(time).RunAsync(response, sink, CancellationToken.None));

        // Finalized via Complete (not Fail) so the user sees the friendly text.
        sink.Completed.ShouldBeTrue();
        sink.Failed.ShouldBeFalse();
        sink.TerminalCount.ShouldBe(1);
        sink.FinalText.ShouldNotBeNull();
        sink.FinalText!.ShouldContain(ProviderMessage);
        sink.FinalText!.ShouldContain("partial");
    }

    [Fact]
    public async Task ProviderUnavailable_MidStream_Logs_Warning_Without_Exception()
    {
        var time = Clock();
        var logger = new RecordingLogger();
        var sink = new RecordingResponseSink(FineGrained);
        var response = new ChatResponse(
            AsyncSequence.ThenThrow(
                new LlmProviderUnavailableException("LLM provider unavailable (HttpRequestException)."),
                default,
                "par", "tial"));

        await Should.NotThrowAsync(
            () => Pump(time, logger).RunAsync(response, sink, CancellationToken.None));

        // A Warning is emitted, but it carries no exception (no stack noise).
        var warnings = logger.EntriesAt(LogLevel.Warning).ToList();
        warnings.ShouldNotBeEmpty();
        warnings.All(e => e.Exception == null).ShouldBeTrue();
    }

    [Fact]
    public async Task ProviderUnavailable_EmptyBuffer_FinalText_Equals_Message()
    {
        var time = Clock();
        var sink = new RecordingResponseSink(FineGrained);
        var response = new ChatResponse(
            AsyncSequence.ThenThrow(
                new LlmProviderUnavailableException("LLM provider unavailable (HttpRequestException)."),
                default));

        await Should.NotThrowAsync(
            () => Pump(time).RunAsync(response, sink, CancellationToken.None));

        sink.Completed.ShouldBeTrue();
        sink.Failed.ShouldBeFalse();
        sink.FinalText.ShouldBe(ProviderMessage);
    }
}
