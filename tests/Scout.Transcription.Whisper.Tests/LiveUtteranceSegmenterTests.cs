namespace Scout.Transcription.Whisper.Tests;

public sealed class LiveUtteranceSegmenterTests
{
    private const int Rate = 16_000;
    private const int Frame = 320;

    private static short Speech(int i) => (short)(16_000 * Math.Sin(2 * Math.PI * 440 * i / Rate));

    private static byte[] Pcm(int samples, Func<int, short> sample)
    {
        var bytes = new byte[samples * 2];
        for (var i = 0; i < samples; i++)
        {
            BitConverter.GetBytes(sample(i)).CopyTo(bytes, i * 2);
        }
        return bytes;
    }

    private static float[] Floats(byte[] pcm)
    {
        var samples = new float[pcm.Length / 2];
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = BitConverter.ToInt16(pcm, i * 2) / 32768f;
        }
        return samples;
    }

    private static byte[] Silence(int samples) => new byte[samples * 2];

    private sealed record Seen(LiveSegmentKind Kind, float[] Samples);

    private static List<Seen> FeedInChunks(LiveUtteranceSegmenter segmenter, byte[] pcm, int chunkBytes)
    {
        var segments = new List<Seen>();
        for (var offset = 0; offset < pcm.Length; offset += chunkBytes)
        {
            segmenter.Feed(pcm.AsSpan(offset, Math.Min(chunkBytes, pcm.Length - offset)), s => segments.Add(new Seen(s.Kind, s.Samples())));
        }
        return segments;
    }

    private static List<Seen> Feed(LiveUtteranceSegmenter segmenter, byte[] pcm) => FeedInChunks(segmenter, pcm, pcm.Length);

    private static Seen? Flush(LiveUtteranceSegmenter segmenter)
    {
        Seen? ended = null;
        segmenter.Flush(s => ended = new Seen(s.Kind, s.Samples()));
        return ended;
    }

    [Fact]
    public void A_Window_Is_Ready_At_750_ms_Then_Every_600_ms()
    {
        var segmenter = new LiveUtteranceSegmenter();
        var speech = Pcm(2 * Rate, Speech);

        var windows = FeedInChunks(segmenter, speech, 3_200);

        windows.ShouldAllBe(s => s.Kind == LiveSegmentKind.WindowReady);
        windows.Select(w => w.Samples.Length).ShouldBe([12_000, 21_600, 31_200]);
        windows[^1].Samples.ShouldBe(Floats(speech)[..31_200]);
        (windows[1].Samples.Length - windows[0].Samples.Length).ShouldBe(
            9_600, "each window holds the utterance so far, 0.6 s more than the one before");
    }

    [Fact]
    public void Speech_Under_160_ms_Is_Ignored()
    {
        var shortClick = new LiveUtteranceSegmenter();
        var shortSegments = FeedInChunks(shortClick, [.. Pcm(7 * Frame, Speech), .. Silence(2 * Rate)], 3_200);
        shortSegments.ShouldBeEmpty("140 ms of speech is not an utterance");
        Flush(shortClick).ShouldBeNull();

        var shortWord = new LiveUtteranceSegmenter();
        var wordSegments = FeedInChunks(shortWord, [.. Pcm(8 * Frame, Speech), .. Silence(2 * Rate)], 3_200);
        wordSegments.Count(s => s.Kind == LiveSegmentKind.UtteranceEnded).ShouldBe(1, "160 ms of speech is an utterance");
    }

    [Fact]
    public void One_Second_Of_Silence_Ends_The_Utterance()
    {
        var segmenter = new LiveUtteranceSegmenter();
        var speech = Pcm(Rate, Speech);
        FeedInChunks(segmenter, speech, 3_200);

        Feed(segmenter, Silence(49 * Frame)).ShouldBeEmpty(
            "980 ms of silence neither ends the utterance nor makes a window ready");
        var ended = Feed(segmenter, Silence(Frame)).ShouldHaveSingleItem();

        ended.Kind.ShouldBe(LiveSegmentKind.UtteranceEnded);
        ended.Samples.ShouldBe(Floats(speech), "the utterance is the speech, without the silence that ended it");
        Flush(segmenter).ShouldBeNull();
    }

    [Fact]
    public void Below_The_Rms_Threshold_Is_Silence()
    {
        var quiet = new LiveUtteranceSegmenter();
        var quietSegments = FeedInChunks(quiet, Pcm(2 * Rate, i => (short)(i % 2 == 0 ? 475 : -475)), 3_200);
        quietSegments.ShouldBeEmpty("an RMS of 0.0145 is below the 0.015 threshold");
        Flush(quiet).ShouldBeNull();

        var audible = new LiveUtteranceSegmenter();
        var audibleSegments = FeedInChunks(audible, Pcm(2 * Rate, i => (short)(i % 2 == 0 ? 508 : -508)), 3_200);
        audibleSegments.Count.ShouldBe(3, "an RMS of 0.0155 is speech");
        Flush(audible).ShouldNotBeNull().Kind.ShouldBe(LiveSegmentKind.UtteranceEnded);
    }

    [Fact]
    public void Only_The_Last_20_Seconds_Are_Kept()
    {
        var segmenter = new LiveUtteranceSegmenter();
        var speech = Pcm(25 * Rate, i => (short)(1_000 + (i % 20_000)));

        var segments = FeedInChunks(segmenter, speech, 32_000);
        if (Flush(segmenter) is { } last)
        {
            segments.Add(last);
        }

        segments.Max(s => s.Samples.Length).ShouldBe(20 * Rate, "nothing beyond 20 seconds accumulates in one utterance");
    }

    [Fact]
    public void An_Utterance_Is_Closed_At_20_Seconds_And_A_New_One_Starts()
    {
        var segmenter = new LiveUtteranceSegmenter();
        var speech = Pcm(25 * Rate, i => (short)(1_000 + (i % 20_000)));

        var segments = FeedInChunks(segmenter, speech, 32_000);
        segments.Add(Flush(segmenter).ShouldNotBeNull());

        var ended = segments.Where(s => s.Kind == LiveSegmentKind.UtteranceEnded).ToList();
        ended.Select(e => e.Samples.Length).ShouldBe([20 * Rate, 5 * Rate]);
        ended.SelectMany(e => e.Samples).ShouldBe(Floats(speech), "no sample is lost across the cut");
    }

    [Fact]
    public void A_Chunk_Split_Mid_Frame_And_Mid_Sample_Is_Reassembled()
    {
        var segmenter = new LiveUtteranceSegmenter();
        var speech = Pcm(Rate, Speech);

        var windows = FeedInChunks(segmenter, speech, 333);
        var ended = Flush(segmenter);

        windows.ShouldHaveSingleItem().Samples.ShouldBe(Floats(speech)[..12_000]);
        ended.ShouldNotBeNull().Samples.ShouldBe(Floats(speech));
    }

    [Fact]
    public void Flush_Ends_The_Open_Utterance()
    {
        var segmenter = new LiveUtteranceSegmenter();
        var speech = Pcm(Rate / 2, Speech);
        Feed(segmenter, speech).ShouldBeEmpty("0.5 s is shorter than the first window");

        var ended = Flush(segmenter);

        ended.ShouldNotBeNull().Kind.ShouldBe(LiveSegmentKind.UtteranceEnded);
        ended.Samples.ShouldBe(Floats(speech));
    }
}
