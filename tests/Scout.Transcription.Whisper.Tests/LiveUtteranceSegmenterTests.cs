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

    private static byte[] QuietRoom() => Pcm(Frame, i => (short)(i % 2 == 0 ? 16 : -16));

    private static byte[] Room(int samples, double rms, int seed)
    {
        var random = new Random(seed);
        return Pcm(samples, _ => (short)((random.NextDouble() * 2 - 1) * rms * Math.Sqrt(3) * 32_768));
    }

    private sealed record Seen(LiveSegmentKind Kind, float[] Samples, LiveTurnEnd? End = null);

    private static List<Seen> FeedInChunks(LiveUtteranceSegmenter segmenter, byte[] pcm, int chunkBytes)
    {
        var segments = new List<Seen>();
        for (var offset = 0; offset < pcm.Length; offset += chunkBytes)
        {
            segmenter.Feed(pcm.AsSpan(offset, Math.Min(chunkBytes, pcm.Length - offset)), s => segments.Add(new Seen(s.Kind, s.Samples(), s.End)));
        }
        return segments;
    }

    private static List<Seen> Feed(LiveUtteranceSegmenter segmenter, byte[] pcm) => FeedInChunks(segmenter, pcm, pcm.Length);

    private static Seen? Flush(LiveUtteranceSegmenter segmenter)
    {
        Seen? ended = null;
        segmenter.Flush(s => ended = new Seen(s.Kind, s.Samples(), s.End));
        return ended;
    }

    [Fact]
    public void A_Window_Is_Ready_At_750_ms_Then_Every_600_ms()
    {
        var segmenter = new LiveUtteranceSegmenter();
        var speech = Pcm(2 * Rate, Speech);
        Feed(segmenter, QuietRoom()).ShouldBeEmpty();

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
        Feed(audible, QuietRoom()).ShouldBeEmpty();
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
        var speech = Pcm(25 * Rate, i => (short)(1_000 + ((i + 10_000) % 20_000)));

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
        Feed(segmenter, QuietRoom()).ShouldBeEmpty();

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

    [Theory]
    [InlineData(0.02)]
    [InlineData(0.035)]
    [InlineData(0.05)]
    public void A_Noisy_Room_Ends_The_Turn_After_1_s_Of_Non_Speech(double room)
    {
        var segmenter = new LiveUtteranceSegmenter();
        var speech = Pcm(Rate, Speech);
        var before = FeedInChunks(segmenter, [.. Room(5 * Rate, room, 1), .. speech, .. Room(49 * Frame, room, 2)], 8_192);

        before.ShouldNotContain(s => s.Kind == LiveSegmentKind.UtteranceEnded,
            $"980 ms of a room at RMS {room} after the speech does not end the turn yet");
        var ended = Feed(segmenter, Room(Frame, room, 3)).ShouldHaveSingleItem();

        ended.Kind.ShouldBe(LiveSegmentKind.UtteranceEnded, $"1 s of a room at RMS {room} is quiet for that room and ends the turn");
        ended.Samples.ShouldBe(Floats(speech), "the turn is the speech, without the room before or after it");
    }

    [Theory]
    [InlineData(0.02)]
    [InlineData(0.05)]
    public void A_Noisy_Room_Alone_Is_Never_A_Turn(double room)
    {
        var segmenter = new LiveUtteranceSegmenter();

        var segments = FeedInChunks(segmenter, Room(10 * Rate, room, 4), 8_192);

        segments.ShouldNotContain(s => s.Kind == LiveSegmentKind.UtteranceEnded,
            $"10 s of a room at RMS {room} with nobody speaking holds no turn to decode and ask");
        Flush(segmenter).ShouldBeNull();
    }

    [Fact]
    public void Speech_Louder_Than_The_Room_Is_Kept_Whole()
    {
        var segmenter = new LiveUtteranceSegmenter();
        byte[] spoken = [.. Pcm(30 * Frame, Speech), .. Room(25 * Frame, 0.03, 5), .. Pcm(30 * Frame, Speech)];

        var segments = FeedInChunks(segmenter, [.. Room(5 * Rate, 0.03, 6), .. spoken, .. Room(Rate, 0.03, 7)], 8_192);

        segments.Where(s => s.Kind == LiveSegmentKind.UtteranceEnded).ShouldHaveSingleItem().Samples.ShouldBe(
            Floats(spoken), "two words with a half-second room pause between them are one turn, kept whole");
    }

    [Fact]
    public void A_Room_That_Rises_During_The_Turn_Still_Ends_It()
    {
        var gainUp = new LiveUtteranceSegmenter();
        var speech = Pcm(Rate, Speech);
        FeedInChunks(gainUp, [.. Room(5 * Rate, 0.02, 8), .. speech, .. Room(49 * Frame, 0.05, 9)], 8_192)
            .ShouldNotContain(s => s.Kind == LiveSegmentKind.UtteranceEnded);
        var ended = Feed(gainUp, Room(Frame, 0.05, 10)).ShouldHaveSingleItem();
        ended.Samples.ShouldBe(Floats(speech), "a room raised from RMS 0.02 to 0.05 in the pause is still quiet for that room");

        var gainUpMore = new LiveUtteranceSegmenter();
        var louder = FeedInChunks(gainUpMore, [.. Room(5 * Rate, 0.02, 11), .. speech, .. Room(4 * Rate, 0.1, 12)], 8_192);
        louder.Where(s => s.Kind == LiveSegmentKind.UtteranceEnded).ShouldHaveSingleItem().Samples[..Rate].ShouldBe(
            Floats(speech), "a room raised to RMS 0.1 is learned within 3 s and its next second of quiet ends the turn, long before 20 s");
    }

    [Fact]
    public void A_Muted_Microphone_Does_Not_Make_The_Room_Quiet()
    {
        var segmenter = new LiveUtteranceSegmenter();
        var speech = Pcm(Rate, Speech);
        var before = FeedInChunks(
            segmenter, [.. Room(5 * Rate, 0.03, 13), .. Silence(3 * Rate), .. speech, .. Room(49 * Frame, 0.03, 14)], 8_192);

        before.ShouldNotContain(s => s.Kind == LiveSegmentKind.UtteranceEnded);
        Feed(segmenter, Room(Frame, 0.03, 15)).ShouldHaveSingleItem().Samples.ShouldBe(
            Floats(speech), "3 s of digital silence sent while Scout answered leave the room at RMS 0.03, so 1 s of it ends the turn");
    }

    [Fact]
    public void Each_Turn_End_Says_Why_It_Ended()
    {
        var quiet = new LiveUtteranceSegmenter();
        var afterQuiet = FeedInChunks(quiet, [.. Pcm(Rate, Speech), .. Silence(Rate)], 3_200);
        afterQuiet.Single(s => s.Kind == LiveSegmentKind.UtteranceEnded).End.ShouldBe(LiveTurnEnd.Quiet);

        var capped = new LiveUtteranceSegmenter();
        var atCap = FeedInChunks(capped, Pcm(21 * Rate, i => (short)(1_000 + (i % 20_000))), 32_000);
        atCap.Single(s => s.Kind == LiveSegmentKind.UtteranceEnded).End.ShouldBe(LiveTurnEnd.Cap);
        Flush(capped).ShouldNotBeNull().End.ShouldBe(LiveTurnEnd.Flush);
    }

    [Theory]
    [InlineData(0.02)]
    [InlineData(0.05)]
    public void A_Noisy_Room_Makes_No_Window_Ready_Before_It_Is_Learned(double room)
    {
        var segmenter = new LiveUtteranceSegmenter();

        var segments = FeedInChunks(segmenter, Room(3 * Rate, room, 16), 8_192);

        segments.ShouldBeEmpty($"the first 3 s of a room at RMS {room} with nobody speaking show no partial");
    }

    [Fact]
    public void Speech_With_Pauses_Between_Syllables_Makes_Its_Window_Ready_Before_The_Room_Is_Learned()
    {
        var segmenter = new LiveUtteranceSegmenter();
        var syllables = Pcm(Rate, i => i % 5_000 < 3_000 ? Speech(i) : (short)(i % 2 == 0 ? 400 : -400));

        var windows = FeedInChunks(segmenter, syllables, 8_192);

        windows.ShouldHaveSingleItem().Kind.ShouldBe(LiveSegmentKind.WindowReady);
        windows[0].Samples.ShouldBe(Floats(syllables)[..12_000],
            "187.5 ms syllables with 125 ms dips to RMS 0.012 from the first frame are speech, so their partial is decoded at 0.75 s");
    }

    private static short Music(int i) => (short)(5_550 * Math.Sin(2 * Math.PI * 220 * i / Rate));

    private static short SoftSpeech(int i) => (short)(8_800 * Math.Sin(2 * Math.PI * 440 * i / Rate));

    [Fact]
    public void A_System_Audio_Lane_With_Digital_Silence_After_Music_Still_Hears_Softer_Speech_As_One_Turn()
    {
        var segmenter = new LiveUtteranceSegmenter();
        var speech = Pcm(Rate, SoftSpeech);

        var segments = FeedInChunks(
            segmenter, [.. Pcm(4 * Rate, Music), .. Silence(4 * Rate), .. speech, .. Silence(Rate)], 8_192);

        segments.Where(s => s.Kind == LiveSegmentKind.UtteranceEnded).ShouldHaveSingleItem().Samples.ShouldBe(
            Floats(speech), "4 s of digital silence after music at RMS 0.12 leave speech at RMS 0.19 audible as one turn");
    }

    [Fact]
    public void Speech_Right_After_Music_Is_Heard_As_One_Turn()
    {
        var segmenter = new LiveUtteranceSegmenter();
        var speech = Pcm(Rate, SoftSpeech);

        var segments = FeedInChunks(segmenter, [.. Pcm(4 * Rate, Music), .. speech, .. Silence(Rate)], 8_192);

        segments.Where(s => s.Kind == LiveSegmentKind.UtteranceEnded).ShouldHaveSingleItem().Samples.ShouldBe(
            Floats(speech), "speech at RMS 0.19 right after music at RMS 0.12 is one turn, not quiet for a room the music set");
    }
}
