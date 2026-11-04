using System.Runtime.CompilerServices;
using Microsoft.Extensions.Time.Testing;
using Scout.Conversation.Abstractions;

namespace Scout.Transcription.Whisper.Tests;

public sealed class WhisperAudioTranscriberLifetimeTests
{
    private static Stream Fixture() => File.OpenRead(Path.Combine(AppContext.BaseDirectory, "Fixtures", "tone-440hz-1500ms.ogg"));

    private sealed class Handle(Action onDispose) : IDisposable
    {
        public void Dispose() => onDispose();
    }

    private static async IAsyncEnumerable<TranscribedSegment> Heard(float[] samples, [EnumeratorCancellation] CancellationToken ct)
    {
        await Task.Yield();
        yield return new TranscribedSegment(TimeSpan.Zero, TimeSpan.FromSeconds(1), "heard");
    }

    [Fact]
    public async Task A_Second_Voice_Message_Does_Not_Enter_The_Model_Until_The_First_Releases_It()
    {
        var entered = new SemaphoreSlim(0);
        var proceed = new SemaphoreSlim(0);
        var inside = 0;
        var mostAtOnce = 0;
        async IAsyncEnumerable<TranscribedSegment> Held(float[] samples, [EnumeratorCancellation] CancellationToken ct)
        {
            mostAtOnce = Math.Max(mostAtOnce, Interlocked.Increment(ref inside));
            entered.Release();
            await proceed.WaitAsync(ct);
            Interlocked.Decrement(ref inside);
            yield return new TranscribedSegment(TimeSpan.Zero, TimeSpan.FromSeconds(1), "heard");
        }

        using var transcriber = new WhisperAudioTranscriber(
            "model.bin",
            _ => new WhisperAudioTranscriber.LoadedModel(Held, new Handle(() => { })),
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
    public async Task A_Voice_Message_Waits_At_Most_30_Seconds_For_A_Recording_Holding_The_Model_And_Is_Refused_As_Busy()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 28, 9, 0, 0, TimeSpan.Zero));
        var entered = new SemaphoreSlim(0);
        var proceed = new SemaphoreSlim(0);
        async IAsyncEnumerable<TranscribedSegment> Held(float[] samples, [EnumeratorCancellation] CancellationToken ct)
        {
            entered.Release();
            await proceed.WaitAsync(ct);
            yield return new TranscribedSegment(TimeSpan.Zero, TimeSpan.FromSeconds(1), "heard");
        }

        using var transcriber = new WhisperAudioTranscriber(
            "model.bin",
            _ => new WhisperAudioTranscriber.LoadedModel(Held, new Handle(() => { })),
            clock);
        await using var recording = Wav16k(16_000);
        var holding = Task.Run(async () =>
        {
            var heard = new List<string>();
            await foreach (var segment in transcriber.TranscribeSegmentsAsync(recording, TimeSpan.FromMinutes(60), CancellationToken.None))
            {
                heard.Add(segment.Text);
            }

            return heard;
        });
        (await entered.WaitAsync(TimeSpan.FromSeconds(5))).ShouldBeTrue("the recording must hold the model");

        await using var voice = Fixture();
        var waiting = transcriber.TranscribeAsync(voice, "audio/ogg", CancellationToken.None);
        await Task.Delay(100);
        clock.Advance(TimeSpan.FromSeconds(29));
        await Task.Delay(100);
        waiting.IsCompleted.ShouldBeFalse("a voice message waits up to 30 seconds for the model");
        clock.Advance(TimeSpan.FromSeconds(1));
        await Should.ThrowAsync<TranscriptionBusyException>(() => waiting.WaitAsync(TimeSpan.FromSeconds(5)));

        proceed.Release();
        (await holding.WaitAsync(TimeSpan.FromSeconds(5))).ShouldBe(["heard"]);
        await using var next = Fixture();
        var after = transcriber.TranscribeAsync(next, "audio/ogg", CancellationToken.None);
        (await entered.WaitAsync(TimeSpan.FromSeconds(5))).ShouldBeTrue("the refused message must not keep the model");
        proceed.Release();
        (await after.WaitAsync(TimeSpan.FromSeconds(5))).ShouldBe("heard");
    }

    private static MemoryStream Wav16k(int samples)
    {
        var file = new MemoryStream();
        var w = new BinaryWriter(file);
        w.Write("RIFF"u8);
        w.Write(36 + (samples * 2));
        w.Write("WAVEfmt "u8);
        w.Write(16);
        w.Write((short)1);
        w.Write((short)1);
        w.Write(16_000);
        w.Write(32_000);
        w.Write((short)2);
        w.Write((short)16);
        w.Write("data"u8);
        w.Write(samples * 2);
        w.Write(new byte[samples * 2]);
        file.Position = 0;
        return file;
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
                return new WhisperAudioTranscriber.LoadedModel(Heard, new Handle(() => unloads++));
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
