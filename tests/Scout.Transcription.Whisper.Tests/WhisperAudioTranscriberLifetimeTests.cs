using Microsoft.Extensions.Time.Testing;

namespace Scout.Transcription.Whisper.Tests;

public sealed class WhisperAudioTranscriberLifetimeTests
{
    private static Stream Fixture() => File.OpenRead(Path.Combine(AppContext.BaseDirectory, "Fixtures", "tone-440hz-1500ms.ogg"));

    private sealed class Handle(Action onDispose) : IDisposable
    {
        public void Dispose() => onDispose();
    }

    [Fact]
    public async Task A_Second_Voice_Message_Does_Not_Enter_The_Model_Until_The_First_Releases_It()
    {
        var entered = new SemaphoreSlim(0);
        var proceed = new SemaphoreSlim(0);
        var inside = 0;
        var mostAtOnce = 0;
        using var transcriber = new WhisperAudioTranscriber(
            "model.bin",
            _ => new WhisperAudioTranscriber.LoadedModel(
                async (_, ct) =>
                {
                    mostAtOnce = Math.Max(mostAtOnce, Interlocked.Increment(ref inside));
                    entered.Release();
                    await proceed.WaitAsync(ct);
                    Interlocked.Decrement(ref inside);
                    return "heard";
                },
                new Handle(() => { })),
            TimeProvider.System);

        await using var firstAudio = Fixture();
        await using var secondAudio = Fixture();
        var first = transcriber.TranscribeAsync(firstAudio, "audio/ogg", CancellationToken.None);
        (await entered.WaitAsync(TimeSpan.FromSeconds(5))).ShouldBeTrue("the first message must reach the model");
        var second = transcriber.TranscribeAsync(secondAudio, "audio/ogg", CancellationToken.None);

        (await entered.WaitAsync(TimeSpan.FromMilliseconds(500))).ShouldBeFalse(
            "the second message must wait while the first holds the model");

        proceed.Release();
        (await first).ShouldBe("heard");
        (await entered.WaitAsync(TimeSpan.FromSeconds(5))).ShouldBeTrue("the second message enters once the first released");
        proceed.Release();
        (await second).ShouldBe("heard");
        mostAtOnce.ShouldBe(1);
    }

    [Fact]
    public async Task The_Model_Unloads_After_10_Idle_Minutes_And_Reloads_On_The_Next_Voice_Message()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 28, 9, 0, 0, TimeSpan.Zero));
        var loads = 0;
        var unloads = 0;
        using var transcriber = new WhisperAudioTranscriber(
            "model.bin",
            _ =>
            {
                loads++;
                return new WhisperAudioTranscriber.LoadedModel((_, _) => Task.FromResult("heard"), new Handle(() => unloads++));
            },
            clock);

        await using (var audio = Fixture())
        {
            await transcriber.TranscribeAsync(audio, "audio/ogg", CancellationToken.None);
        }
        clock.Advance(TimeSpan.FromMinutes(9));
        unloads.ShouldBe(0, "the model stays loaded before 10 idle minutes");

        clock.Advance(TimeSpan.FromMinutes(1));
        unloads.ShouldBe(1);

        await using (var audio = Fixture())
        {
            await transcriber.TranscribeAsync(audio, "audio/ogg", CancellationToken.None);
        }
        loads.ShouldBe(2);
    }
}
