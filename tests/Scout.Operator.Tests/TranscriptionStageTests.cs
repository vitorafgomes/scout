using System.Runtime.CompilerServices;
using Microsoft.Extensions.Time.Testing;
using Scout.Conversation.Abstractions;
using Scout.Operator.Tests.Fakes;

namespace Scout.Operator.Tests;

public sealed class TranscriptionStageTests : IDisposable
{
    private const string Recording = "owner-recording-1a.mp4";

    private const int TenMinutes = 600_000;

    private readonly string _media = Directory.CreateTempSubdirectory("scout-transcription-").FullName;

    private readonly FakeTimeProvider _clock = new(Given.Now);

    public TranscriptionStageTests()
    {
        File.WriteAllBytes(Path.Combine(_media, Recording), [1, 2, 3]);
    }

    public void Dispose() => Directory.Delete(_media, recursive: true);

    private static OperatorProjectState Transcribing()
        => Given.Project(status: "transcribing") with { Origin = "desktop", Format = "own_video", RecordingFile = Recording };

    private RecordingControlPlane Holding(OperatorProjectState project)
    {
        var controlPlane = new RecordingControlPlane { MediaDirectory = _media };
        controlPlane.Store(project);
        return controlPlane;
    }

    private Task Run(RecordingControlPlane controlPlane, FakeProcessRunner runner, IAudioTranscriber? transcriber)
        => Given.Pipeline(controlPlane, Given.YouTube(), ScriptedChat.Answering(), Substitute.For<IOriginNotifier>(), time: _clock, runner: runner, transcriber: transcriber)
            .RunAsync(new QueuedProject(Transcribing(), null), CancellationToken.None);

    private static FakeProcessRunner Recorded(int ms = TenMinutes, bool picture = true, bool sound = true)
        => new() { ProbedMs = ms, ReportsPicture = picture, ReportsSound = sound };

    private static Task<OperatorProjectState> Stored(RecordingControlPlane controlPlane)
        => controlPlane.GetProjectAsync("p1", CancellationToken.None);

    [Fact]
    public async Task The_Transcript_Is_The_Script_One_Section_Per_Segment_With_Its_Times()
    {
        var controlPlane = Holding(Transcribing());
        var transcriber = new ScriptedTranscriber
        {
            Heard = _ => ScriptedTranscriber.Segments((0, 1200, "  Hello there. "), (1200, 1500, "   "), (1500, 4000, " Today we bake bread.")),
        };

        await Run(controlPlane, Recorded(), transcriber);

        var saved = await Stored(controlPlane);
        (saved.Status, saved.Error, saved.RecordingMs).ShouldBe(("script_ready", (string?)null, TenMinutes));
        var script = saved.Script.ShouldNotBeNull();
        (script.Hook, script.CallToAction, script.Description).ShouldBe((string.Empty, string.Empty, string.Empty));
        script.Sections.Select(s => (s.Heading, s.Narration, s.StartMs, s.EndMs)).ShouldBe([
            (string.Empty, "Hello there.", 0, 1200),
            (string.Empty, "Today we bake bread.", 1500, 4000),
        ]);
        transcriber.Limit.ShouldBe(TimeSpan.FromMinutes(60));
    }

    [Fact]
    public async Task The_Sound_Is_Taken_Out_As_16_kHz_Mono_Wav_Inside_The_Media_Folder_And_Removed_After()
    {
        var controlPlane = Holding(Transcribing());
        var runner = Recorded();
        var transcriber = new ScriptedTranscriber { Heard = _ => ScriptedTranscriber.Segments((0, 900, "Hello.")) };

        await Run(controlPlane, runner, transcriber);

        var extraction = runner.Renders.ShouldHaveSingleItem();
        extraction.SkipLast(1).ShouldBe([
            "-hide_banner", "-nostdin", "-y", "-i", Path.Combine(_media, Recording), "-vn", "-ac", "1", "-ar", "16000", "-c:a", "pcm_s16le",
        ]);
        Path.GetDirectoryName(extraction[^1]).ShouldBe(_media);
        extraction[^1].ShouldEndWith(".recording.wav");
        transcriber.ReadFrom.ShouldBe(Path.Combine(_media, "recording.wav"));
        transcriber.FileThere.ShouldBeTrue("the transcriber reads the WAV the extraction wrote");
        Directory.GetFiles(_media).Select(Path.GetFileName).ShouldBe([Recording], "the WAV is removed once the transcript is saved");
    }

    [Fact]
    public async Task Progress_Is_Saved_At_Most_Once_A_Minute_With_The_Transcript_So_Far()
    {
        var controlPlane = Holding(Transcribing());
        async IAsyncEnumerable<TranscribedSegment> Paced([EnumeratorCancellation] CancellationToken ct = default)
        {
            foreach (var (afterSeconds, index) in new[] { (0, 1), (30, 2), (40, 3), (20, 4), (50, 5) })
            {
                await Task.Yield();
                _clock.Advance(TimeSpan.FromSeconds(afterSeconds));
                yield return new TranscribedSegment(TimeSpan.FromSeconds(index * 10), TimeSpan.FromSeconds((index * 10) + 5), $"Part {index}.");
            }
        }

        await Run(controlPlane, Recorded(), new ScriptedTranscriber { Heard = ct => Paced(ct) });

        controlPlane.Saves.Select(s => (s.Project.Status, s.Project.RecordingMs, string.Join(" ", s.Project.Script?.Sections.Select(x => x.Narration) ?? [])))
            .ShouldBe([
                ("transcribing", TenMinutes, string.Empty),
                ("transcribing", TenMinutes, "Part 1. Part 2. Part 3."),
                ("transcribing", TenMinutes, "Part 1. Part 2. Part 3. Part 4. Part 5."),
                ("script_ready", TenMinutes, "Part 1. Part 2. Part 3. Part 4. Part 5."),
            ]);
    }

    [Fact]
    public async Task An_Extraction_Ffmpeg_Refuses_Fails_The_Project_And_Whisper_Is_Never_Called()
    {
        var controlPlane = Holding(Transcribing());
        var runner = new FakeProcessRunner
        {
            ProbedMs = TenMinutes,
            ReportsPicture = true,
            Answer = (_, arguments) => arguments.Contains("pcm_s16le")
                ? new ProcessResult(1, string.Empty, "Stream mapping:\nowner-recording-1a.mp4: Invalid data found when processing input\n")
                : null,
        };
        var transcriber = new ScriptedTranscriber();

        await Run(controlPlane, runner, transcriber);

        var saved = await Stored(controlPlane);
        (saved.Status, saved.Error, saved.Script).ShouldBe((
            "failed",
            "Scout could not take the sound out of the recording (ffmpeg: owner-recording-1a.mp4: Invalid data found when processing input)",
            (OperatorScript?)null));
        transcriber.Calls.ShouldBe(0);
        Directory.GetFiles(_media).Select(Path.GetFileName).ShouldBe([Recording]);
    }

    [Theory]
    [InlineData("The model file is cut short.", "IOException: The model file is cut short.")]
    [InlineData("  Bad magic number in the model file, which is not a ggml model at all and cannot be loaded by Whisper, so check it again and download it once more\r\nsecond line", "IOException: Bad magic number in the model file, which is not a ggml model at all and cannot be loaded by Whisper, so check it again")]
    public async Task A_Transcriber_That_Throws_Fails_With_Could_Not_Transcribe_Naming_The_Cause_And_Removes_The_Wav(string message, string cause)
    {
        var controlPlane = Holding(Transcribing());
        var transcriber = new ScriptedTranscriber { Heard = _ => ScriptedTranscriber.Throwing(new IOException(message)) };

        await Run(controlPlane, Recorded(), transcriber);

        var saved = await Stored(controlPlane);
        (saved.Status, saved.Error, saved.Script).ShouldBe(("failed", $"Scout could not transcribe the recording ({cause})", (OperatorScript?)null));
        transcriber.Calls.ShouldBe(1);
        Directory.GetFiles(_media).Select(Path.GetFileName).ShouldBe([Recording]);
    }

    [Fact]
    public async Task A_Progress_Save_That_Fails_Cancels_The_Transcription_Still_Running_And_Fails_The_Project_With_The_Saves_Sentence()
    {
        var controlPlane = new RecordingControlPlane
        {
            MediaDirectory = _media,
            FailOnProject = p => p.Status == "transcribing" && p.Script?.Sections.Count > 0,
        };
        controlPlane.Store(Transcribing());
        var running = CancellationToken.None;
        async IAsyncEnumerable<TranscribedSegment> UntilCancelled([EnumeratorCancellation] CancellationToken ct = default)
        {
            running = ct;
            await Task.Yield();
            _clock.Advance(TimeSpan.FromMinutes(1));
            yield return new TranscribedSegment(TimeSpan.Zero, TimeSpan.FromSeconds(2), "Hello.");
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        }

        await Run(controlPlane, Recorded(), new ScriptedTranscriber { Heard = ct => UntilCancelled(ct) }).WaitAsync(TimeSpan.FromSeconds(10));

        running.IsCancellationRequested.ShouldBeTrue("the transcription still running must be cancelled once the stage stops");
        var saved = await Stored(controlPlane);
        (saved.Status, saved.Error, saved.Script).ShouldBe(("failed", "Scout could not reach its control plane.", (OperatorScript?)null));
        Directory.GetFiles(_media).Select(Path.GetFileName).ShouldBe([Recording]);
    }

    [Fact]
    public async Task A_Progress_Save_The_Control_Plane_Refuses_As_No_Longer_Transcribing_Stops_The_Run_Without_Failing_The_Project()
    {
        var controlPlane = new RecordingControlPlane { MediaDirectory = _media, NoLongerTranscribingOn = save => save == 2 };
        controlPlane.Store(Transcribing());
        async IAsyncEnumerable<TranscribedSegment> Paced([EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.Yield();
            _clock.Advance(TimeSpan.FromMinutes(1));
            yield return new TranscribedSegment(TimeSpan.Zero, TimeSpan.FromSeconds(2), "Hello.");
            yield return new TranscribedSegment(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), "Again.");
        }

        await Run(controlPlane, Recorded(), new ScriptedTranscriber { Heard = ct => Paced(ct) });

        controlPlane.Saves.Select(s => s.Project.Status).ShouldBe(["transcribing", "transcribing"], "a refused save stops the run quietly, with no failed save");
        (await Stored(controlPlane)).Status.ShouldBe("transcribing");
        Directory.GetFiles(_media).Select(Path.GetFileName).ShouldBe([Recording]);
    }

    [Fact]
    public async Task A_Long_Silence_Still_Saves_Progress_Once_A_Minute()
    {
        var controlPlane = Holding(Transcribing());
        async IAsyncEnumerable<TranscribedSegment> Silent([EnumeratorCancellation] CancellationToken ct = default)
        {
            for (var k = 1; k <= 4; k++)
            {
                await Task.Yield();
                _clock.Advance(TimeSpan.FromSeconds(40));
                yield return new TranscribedSegment(TimeSpan.FromSeconds(k * 40), TimeSpan.FromSeconds((k * 40) + 30), "  ");
            }

            yield return new TranscribedSegment(TimeSpan.FromSeconds(170), TimeSpan.FromSeconds(175), "Done.");
        }

        await Run(controlPlane, Recorded(), new ScriptedTranscriber { Heard = ct => Silent(ct) });

        controlPlane.Saves.Select(s => (s.Project.Status, s.Project.Script?.Sections.Count))
            .ShouldBe([("transcribing", 0), ("transcribing", 0), ("transcribing", 0), ("script_ready", 1)]);
    }

    [Fact]
    public async Task A_Transcription_Past_Its_Time_Limit_Fails_With_The_Minutes()
    {
        var controlPlane = Holding(Transcribing());
        var listening = new TaskCompletionSource();
        async IAsyncEnumerable<TranscribedSegment> Endless([EnumeratorCancellation] CancellationToken ct = default)
        {
            yield return new TranscribedSegment(TimeSpan.Zero, TimeSpan.FromSeconds(2), "Hello.");
            listening.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        }

        var run = Run(controlPlane, Recorded(), new ScriptedTranscriber { Heard = ct => Endless(ct) });
        await listening.Task.WaitAsync(TimeSpan.FromSeconds(10));
        _clock.Advance(TimeSpan.FromMinutes(70) - TimeSpan.FromSeconds(1));
        await Task.Delay(100);
        run.IsCompleted.ShouldBeFalse("a ten-minute recording may take 10 minutes plus six times its length");
        _clock.Advance(TimeSpan.FromSeconds(1));
        await run.WaitAsync(TimeSpan.FromSeconds(10));

        var saved = await Stored(controlPlane);
        (saved.Status, saved.Error, saved.Script).ShouldBe(("failed", "Scout stopped transcribing the recording after 70 minutes.", (OperatorScript?)null));
        Directory.GetFiles(_media).Select(Path.GetFileName).ShouldBe([Recording]);
    }

    [Theory]
    [InlineData(false, true, TenMinutes, "The recording has no picture; attach a video file.")]
    [InlineData(true, false, TenMinutes, "The recording has no sound to transcribe.")]
    [InlineData(true, true, 3_601_000, "The recording is longer than 60 minutes; Scout takes at most 60.")]
    public async Task A_Recording_Without_A_Picture_Without_Sound_Or_Over_60_Minutes_Is_Refused_Before_Extraction(
        bool picture, bool sound, int ms, string reason)
    {
        var controlPlane = Holding(Transcribing());
        var runner = Recorded(ms, picture, sound);
        var transcriber = new ScriptedTranscriber();

        await Run(controlPlane, runner, transcriber);

        var saved = await Stored(controlPlane);
        (saved.Status, saved.Error).ShouldBe(("failed", reason));
        runner.Renders.ShouldBeEmpty();
        transcriber.Calls.ShouldBe(0);
        controlPlane.Saves.Select(s => s.Project.Status).ShouldBe(["failed"]);
    }

    [Fact]
    public async Task Without_A_Speech_To_Text_Model_The_Project_Fails_With_The_Local_Models_Sentence()
    {
        var controlPlane = Holding(Transcribing());
        var runner = Recorded();

        await Run(controlPlane, runner, transcriber: null);

        var saved = await Stored(controlPlane);
        (saved.Status, saved.Error).ShouldBe(("failed", "Scout has no speech-to-text model on this machine; download one in Models, Transcription."));
        runner.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_Recording_With_No_Speech_Fails_With_Heard_No_Speech()
    {
        var controlPlane = Holding(Transcribing());
        var transcriber = new ScriptedTranscriber { Heard = _ => ScriptedTranscriber.Segments((0, 30_000, " "), (30_000, 60_000, string.Empty)) };

        await Run(controlPlane, Recorded(), transcriber);

        var saved = await Stored(controlPlane);
        (saved.Status, saved.Error, saved.Script).ShouldBe(("failed", "Scout heard no speech in the recording.", (OperatorScript?)null));
        Directory.GetFiles(_media).Select(Path.GetFileName).ShouldBe([Recording]);
    }

    [Fact]
    public async Task A_Recording_Ffmpeg_Cannot_Read_Fails_With_A_Sentence_Naming_It_Before_Extraction()
    {
        var controlPlane = Holding(Transcribing());
        var runner = new FakeProcessRunner
        {
            Answer = (_, arguments) => arguments[^2] == "-i" && arguments[^1].EndsWith(Recording, StringComparison.Ordinal)
                ? new ProcessResult(1, string.Empty, "owner-recording-1a.mp4: Invalid data found when processing input\n")
                : null,
        };
        var transcriber = new ScriptedTranscriber();

        await Run(controlPlane, runner, transcriber);

        var saved = await Stored(controlPlane);
        (saved.Status, saved.Error).ShouldBe(("failed", "Scout could not read the length of owner-recording-1a.mp4."));
        runner.Renders.ShouldBeEmpty();
        transcriber.Calls.ShouldBe(0);
    }
}
