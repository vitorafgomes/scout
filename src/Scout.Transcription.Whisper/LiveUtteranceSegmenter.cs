namespace Scout.Transcription.Whisper;

/// <summary>
/// Cuts a live 16 kHz PCM16 stream into utterances with an RMS energy check per
/// 20 ms frame, and says when the utterance so far is ready for a partial decode
/// (cumulative windows from the utterance's start, the first at 0.75 s, then every
/// 0.6 s; a window due during a pause waits for the speech to resume) and when it
/// has ended (after 1 s without speech, or on <see cref="Flush"/>, or when it
/// reaches 20 s, after which a new utterance starts at the next frame). A frame is
/// speech when its RMS is at least 0.015 and at least three times the room, the
/// quietest frame of the last 3 s counted as at most 0.05, so the threshold never
/// rises above 0.15 after music or other loud sound; digital silence, a muted
/// microphone, is not the room. Until 3 s of sound have been heard the room is taken as quiet, and a turn
/// begun then is kept only if it still holds enough speech for the room learned
/// when it ends; its windows wait until it holds 160 ms at least three times the
/// quietest frame heard so far, so a noisy room never makes one ready. Speech
/// shorter than 160 ms never yields anything.
/// </summary>
internal sealed class LiveUtteranceSegmenter
{
    internal const int SampleRate = 16_000;
    internal const int FrameSamples = 320;
    internal const float RmsThreshold = 0.015f;
    internal const int MinSpeechFrames = 8;
    internal const int EndSilenceFrames = 50;
    internal const int FirstWindowSamples = 12_000;
    internal const int HopSamples = 9_600;
    internal const int RingSamples = 320_000;
    internal const float RoomFactor = 3f;
    internal const int RoomFrames = 150;
    internal const float RoomCap = 0.05f;

    private readonly float[] _frame = new float[FrameSamples];
    private readonly float[] _ring = new float[RingSamples];
    private readonly float[] _room = Enumerable.Repeat(RmsThreshold / RoomFactor, RoomFrames).ToArray();
    private readonly float[] _turnRms = new float[RingSamples / FrameSamples];
    private int _roomNext;
    private float _quietestHeard = float.MaxValue;
    private int _roomHeard;
    private int _turnFrames;
    private bool _turnBeganUnlearned;
    private bool _turnWindowsHeard;
    private float _loudestRms;
    private int _frameFill;
    private byte? _oddByte;
    private bool _open;
    private int _ringStart;
    private int _ringCount;
    private long _utteranceSamples;
    private long _nextWindowAt;
    private int _speechFrames;
    private int _trailingSilenceFrames;

    /// <summary>The loudest frame RMS heard so far.</summary>
    internal float LoudestRms => _loudestRms;

    /// <summary>The room: the quietest frame RMS of the last 3 s, at most 0.005 until 3 s of sound have been heard.</summary>
    internal float Room => _room.Min();

    /// <summary>Consumes PCM16 little-endian bytes and hands each segment to <paramref name="ready"/> as it becomes ready.</summary>
    public void Feed(ReadOnlySpan<byte> pcm16, Action<LiveSegment> ready)
    {
        var i = 0;
        if (_oddByte is { } low && pcm16.Length > 0)
        {
            Push((short)(low | (pcm16[0] << 8)) / 32768f, ready);
            _oddByte = null;
            i = 1;
        }
        for (; i + 1 < pcm16.Length; i += 2)
        {
            Push((short)(pcm16[i] | (pcm16[i + 1] << 8)) / 32768f, ready);
        }
        if (i < pcm16.Length)
        {
            _oddByte = pcm16[i];
        }
    }

    /// <summary>Ends the open utterance and hands it to <paramref name="ready"/> if it holds enough speech.</summary>
    public void Flush(Action<LiveSegment> ready)
    {
        if (_open && HeardEnough())
        {
            ready(Ended(LiveTurnEnd.Flush));
        }
        Close();
        _frameFill = 0;
        _oddByte = null;
    }

    private void Push(float sample, Action<LiveSegment> ready)
    {
        _frame[_frameFill++] = sample;
        if (_open)
        {
            Append(sample);
            if (_utteranceSamples >= _nextWindowAt && _trailingSilenceFrames == 0 && _speechFrames >= MinSpeechFrames && _turnWindowsHeard)
            {
                _nextWindowAt = _utteranceSamples + HopSamples;
                ready(new LiveSegment(LiveSegmentKind.WindowReady, _ringCount, this));
            }
        }
        if (_frameFill == FrameSamples)
        {
            _frameFill = 0;
            EndFrame(ready);
        }
    }

    private void EndFrame(Action<LiveSegment> ready)
    {
        var rms = Rms(_frame);
        _loudestRms = MathF.Max(_loudestRms, rms);
        if (rms > 0f)
        {
            _room[_roomNext] = rms;
            _roomNext = (_roomNext + 1) % RoomFrames;
            _roomHeard = Math.Min(_roomHeard + 1, RoomFrames);
            _quietestHeard = MathF.Min(_quietestHeard, rms);
        }
        var speech = rms >= Threshold();
        if (!_open)
        {
            if (!speech)
            {
                return;
            }
            _open = true;
            _turnBeganUnlearned = _roomHeard < RoomFrames;
            _nextWindowAt = FirstWindowSamples;
            foreach (var sample in _frame)
            {
                Append(sample);
            }
        }
        _turnRms[_turnFrames++] = rms;
        _turnWindowsHeard = _turnWindowsHeard || WindowHeard();

        if (speech)
        {
            _speechFrames++;
            _trailingSilenceFrames = 0;
            CloseIfFull(ready);
            return;
        }

        if (++_trailingSilenceFrames < EndSilenceFrames)
        {
            CloseIfFull(ready);
            return;
        }
        if (HeardEnough())
        {
            ready(Ended(LiveTurnEnd.Quiet));
        }
        Close();
    }

    private void CloseIfFull(Action<LiveSegment> ready)
    {
        if (_utteranceSamples < RingSamples)
        {
            return;
        }
        if (HeardEnough())
        {
            ready(Ended(LiveTurnEnd.Cap));
        }
        Close();
    }

    private bool WindowHeard()
    {
        if (!_turnBeganUnlearned)
        {
            return true;
        }
        return SpeechFramesAbove(ThresholdOver(_quietestHeard)) >= MinSpeechFrames;
    }

    private float Threshold() => ThresholdOver(Room);

    private static float ThresholdOver(float room) => MathF.Max(RmsThreshold, RoomFactor * MathF.Min(room, RoomCap));

    private bool HeardEnough()
    {
        if (!_turnBeganUnlearned)
        {
            return _speechFrames >= MinSpeechFrames;
        }
        return SpeechFramesAbove(Threshold()) >= MinSpeechFrames;
    }

    private int SpeechFramesAbove(float threshold)
    {
        var frames = 0;
        foreach (var rms in _turnRms.AsSpan(0, _turnFrames))
        {
            if (rms >= threshold)
            {
                frames++;
            }
        }
        return frames;
    }

    private LiveSegment Ended(LiveTurnEnd end)
        => new(LiveSegmentKind.UtteranceEnded, _ringCount - _trailingSilenceFrames * FrameSamples, this, end);

    private void Append(float sample)
    {
        _ring[(_ringStart + _ringCount) % RingSamples] = sample;
        if (_ringCount < RingSamples)
        {
            _ringCount++;
        }
        else
        {
            _ringStart = (_ringStart + 1) % RingSamples;
        }
        _utteranceSamples++;
    }

    internal float[] Snapshot(int length)
    {
        var samples = new float[Math.Max(0, length)];
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = _ring[(_ringStart + i) % RingSamples];
        }
        return samples;
    }

    private void Close()
    {
        _open = false;
        _ringStart = 0;
        _ringCount = 0;
        _utteranceSamples = 0;
        _speechFrames = 0;
        _trailingSilenceFrames = 0;
        _turnFrames = 0;
        _turnWindowsHeard = false;
    }

    private static float Rms(float[] frame)
    {
        var sum = 0f;
        foreach (var sample in frame)
        {
            sum += sample * sample;
        }
        return MathF.Sqrt(sum / frame.Length);
    }
}

/// <summary>What <see cref="LiveUtteranceSegmenter"/> found ready.</summary>
internal enum LiveSegmentKind
{
    /// <summary>The utterance so far is ready for a partial decode.</summary>
    WindowReady,

    /// <summary>The utterance has ended and is ready for its final decode.</summary>
    UtteranceEnded,
}

/// <summary>Why <see cref="LiveUtteranceSegmenter"/> ended a turn.</summary>
internal enum LiveTurnEnd
{
    /// <summary>One second without speech, relative to the room.</summary>
    Quiet,

    /// <summary>The turn reached 20 seconds.</summary>
    Cap,

    /// <summary>The listen was stopped.</summary>
    Flush,
}

/// <summary>
/// An utterance so far or complete, <paramref name="Length"/> samples long, with
/// <paramref name="End"/> set when it has ended. Its samples are copied only when
/// <see cref="Samples"/> is called, which is valid only inside the callback that
/// received the segment.
/// </summary>
internal readonly record struct LiveSegment(LiveSegmentKind Kind, int Length, LiveUtteranceSegmenter Source, LiveTurnEnd? End = null)
{
    /// <summary>Copies the segment's samples.</summary>
    public float[] Samples() => Source.Snapshot(Length);
}
