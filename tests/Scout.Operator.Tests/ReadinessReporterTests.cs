using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Scout.Llm.Abstractions;
using Scout.Operator.Tests.Fakes;
using Scout.Plugin.YouTube;

namespace Scout.Operator.Tests;

public sealed class ReadinessReporterTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("scout-readiness-").FullName;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private ReadinessReporter Reporter(
        MediaCapabilities media,
        RecordingControlPlane? controlPlane = null,
        string tokenFile = "",
        TimeProvider? time = null,
        ILogger<ReadinessReporter>? logger = null, IProcessRunner? runner = null, ScheduleNotes? schedule = null, PiperSpeech? piper = null,
        OpenversePictures? stock = null, Scout.Conversation.Abstractions.IAudioTranscriber? transcriber = null, RhubarbLipSync? lipSync = null,
        IFaceMouth? faces = null, BuiltInPresenters? presenters = null, VisualGenerator? visuals = null)
        => new(
            controlPlane ?? new RecordingControlPlane(),
            media,
            Options.Create(new YouTubeOptions { TokenFile = tokenFile }),
            time ?? new FakeTimeProvider(Given.Now),
            logger ?? new RecordingLogger<ReadinessReporter>(), Given.Toolset(runner ?? new FakeProcessRunner()),
            piper ?? Given.Piper(new FakeProcessRunner()), schedule, stock, transcriber, lipSync, faces, presenters, visuals);

    private PiperSpeech Bundled()
    {
        var (piper, voices) = Given.BundledVoices(_folder);
        return Given.Piper(new FakeProcessRunner(), piper, voices);
    }

    [Fact]
    public async Task Lists_provider_and_piper_voices()
    {
        var media = new MediaCapabilities(true, true, "OpenAI") { SpeechVoices = ["alloy", "nova"] };

        var readiness = await Reporter(media, piper: Bundled()).CurrentAsync(CancellationToken.None);

        readiness.Voices.ShouldBe([
            new OperatorVoice("OpenAI:alloy", "alloy (OpenAI)", "OpenAI"),
            new OperatorVoice("OpenAI:nova", "nova (OpenAI)", "OpenAI"),
            new OperatorVoice("piper:en_GB-aru-medium#0", "Aru 03 (medium)", "piper"),
            new OperatorVoice("piper:en_GB-aru-medium#1", "Aru 06 (medium)", "piper"),
            new OperatorVoice("piper:en_GB-cori-medium", "Cori (medium)", "piper"),
        ]);
        readiness.SpeechAvailable.ShouldBeTrue();
    }

    [Theory]
    [InlineData("None", false, "No AI Source is connected; narration uses the bundled voices and visuals become text cards.")]
    [InlineData("Gemini", false, "Gemini has no speech or image service in Scout; narration uses the bundled voices and visuals become text cards.")]
    [InlineData("AzureOpenAI", true, "AzureOpenAI has no speech service in Scout; narration uses the bundled voices unless you attach your own audio.")]
    public async Task A_Provider_Without_Speech_Narrates_With_The_Bundled_Voices_And_Says_So(string provider, bool images, string note)
    {
        var readiness = await Reporter(new MediaCapabilities(false, images, provider), piper: Bundled()).CurrentAsync(CancellationToken.None);

        readiness.SpeechAvailable.ShouldBeTrue();
        readiness.Voices.Select(v => v.Source).ShouldBe(["piper", "piper", "piper"]);
        readiness.Notes.ShouldBe([note, "Scout is not signed in to YouTube; research goes on without the channel's own uploads.", "Videos are assembled with ffmpeg and the libopenh264 encoder."]);
    }

    [Fact]
    public async Task Without_an_image_service_the_readiness_says_pictures_come_from_openverse()
    {
        using var http = new OpenverseHttp();
        var stock = new OpenversePictures(http.Factory(), new SteppingTime(Given.Now));

        var withoutImages = await Reporter(new MediaCapabilities(false, false, "Gemini"), stock: stock).CurrentAsync(CancellationToken.None);
        var withImages = await Reporter(new MediaCapabilities(true, true, "OpenAI"), stock: stock).CurrentAsync(CancellationToken.None);
        var withoutStock = await Reporter(new MediaCapabilities(false, false, "Gemini")).CurrentAsync(CancellationToken.None);

        (withoutImages.ImagesAvailable, withoutImages.StockPictures).ShouldBe((false, true));
        withoutImages.Notes[1].ShouldBe("Pictures without an AI image service come from Openverse (CC0, public domain or CC BY, credited in the description).");
        (withImages.StockPictures, withoutStock.StockPictures).ShouldBe((true, false));
        withImages.Notes.ShouldNotContain(n => n.Contains("Openverse"));
        withoutStock.Notes.ShouldNotContain(n => n.Contains("Openverse"));
        http.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task Bundled_Voices_The_Desktop_Named_But_That_Are_Missing_Are_Reported_Unusable()
    {
        var missing = Given.Piper(new FakeProcessRunner(), Path.Combine(_folder, "piper"), Path.Combine(_folder, "voices"));

        var readiness = await Reporter(new MediaCapabilities(false, false, "Gemini"), piper: missing).CurrentAsync(CancellationToken.None);

        (readiness.SpeechAvailable, readiness.Voices.Count).ShouldBe((false, 0));
        readiness.Notes.Take(2).ShouldBe([
            "Gemini has no speech or image service in Scout; narration needs your own audio and visuals become text cards.",
            "The bundled voices are missing or cannot run, so narration needs the connected AI Source or your own audio.",
        ]);
    }

    [Fact]
    public async Task The_Scheduled_Jobs_Scout_Ran_Late_At_Start_Are_Named_In_The_Notes()
    {
        var token = Path.Combine(_folder, "youtube-token.json");
        File.WriteAllText(token, "{}");
        var schedule = new ScheduleNotes { Missed = "Scout was not running for these scheduled jobs and ran them when it started: analytics 09:00." };

        var readiness = await Reporter(new MediaCapabilities(true, true, "OpenAI"), tokenFile: token, schedule: schedule).CurrentAsync(CancellationToken.None);

        readiness.Notes.ShouldBe([
            "Videos are assembled with ffmpeg and the libopenh264 encoder.",
            "Scout was not running for these scheduled jobs and ran them when it started: analytics 09:00.",
        ]);
    }

    [Fact]
    public async Task Gemini_Reports_No_Speech_And_No_Images_With_The_Note_That_Narration_Needs_Own_Audio_And_Visuals_Become_Text_Cards()
    {
        var readiness = await Reporter(new MediaCapabilities(false, false, "Gemini")).CurrentAsync(CancellationToken.None);

        (readiness.SpeechAvailable, readiness.ImagesAvailable, readiness.Provider, readiness.FfmpegAvailable)
            .ShouldBe((false, false, "Gemini", true));
        readiness.Notes.ShouldContain(
            "Gemini has no speech or image service in Scout; narration needs your own audio and visuals become text cards.");
    }

    [Fact]
    public async Task OpenAI_Reports_Speech_And_Images_With_No_Media_Note()
    {
        var token = Path.Combine(_folder, "youtube-token.json");
        File.WriteAllText(token, "{}");

        var readiness = await Reporter(new MediaCapabilities(true, true, "OpenAI"), tokenFile: token).CurrentAsync(CancellationToken.None);

        (readiness.SpeechAvailable, readiness.ImagesAvailable, readiness.Provider, readiness.FfmpegAvailable, readiness.YouTubeSignedIn)
            .ShouldBe((true, true, "OpenAI", true, true));
        readiness.Notes.ShouldBe(["Videos are assembled with ffmpeg and the libopenh264 encoder."]);
        readiness.Voices.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(true, false, "AzureOpenAI has no image service in Scout; visuals become text cards unless you attach a background to the project in Scout Desktop, Production, or connect an AI Source with pictures.")]
    [InlineData(false, true, "AzureOpenAI has no speech service in Scout; narration needs your own audio.")]
    public async Task A_Provider_With_Only_One_Media_Service_Names_The_One_It_Lacks(bool speech, bool images, string note)
    {
        var token = Path.Combine(_folder, "youtube-token.json");
        File.WriteAllText(token, "{}");

        var readiness = await Reporter(new MediaCapabilities(speech, images, "AzureOpenAI"), tokenFile: token).CurrentAsync(CancellationToken.None);

        readiness.Notes.ShouldBe([note, "Videos are assembled with ffmpeg and the libopenh264 encoder."]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("missing-token.json")]
    public async Task Without_A_YouTube_Token_File_On_Disk_The_Report_Says_Scout_Is_Not_Signed_In(string tokenFile)
    {
        var path = tokenFile.Length == 0 ? string.Empty : Path.Combine(_folder, tokenFile);

        var readiness = await Reporter(new MediaCapabilities(true, true, "OpenAI"), tokenFile: path).CurrentAsync(CancellationToken.None);

        readiness.YouTubeSignedIn.ShouldBeFalse();
        readiness.Notes.ShouldBe(["Scout is not signed in to YouTube; research goes on without the channel's own uploads.", "Videos are assembled with ffmpeg and the libopenh264 encoder."]);
    }

    [Fact]
    public async Task With_No_AI_Source_Connected_The_Note_Says_So()
    {
        var readiness = await Reporter(new MediaCapabilities(false, false, "None")).CurrentAsync(CancellationToken.None);

        readiness.Notes.ShouldContain("No AI Source is connected; narration needs your own audio and visuals become text cards.");
    }

    [Fact]
    public async Task The_Report_Goes_Out_At_Start_And_Every_10_Minutes_And_A_Control_Plane_That_Fails_Is_Logged_Once()
    {
        var controlPlane = new RecordingControlPlane { ReportFailure = new InvalidOperationException("Scout could not reach its control plane.") };
        var time = new FakeTimeProvider(Given.Now);
        var logger = new RecordingLogger<ReadinessReporter>();
        using var reporter = Reporter(new MediaCapabilities(false, false, "Gemini"), controlPlane, time: time, logger: logger);

        await reporter.StartAsync(CancellationToken.None);
        await WaitFor(() => controlPlane.Reports.Count == 1);
        time.Advance(TimeSpan.FromMinutes(10) - TimeSpan.FromSeconds(1));
        await Task.Delay(50);
        var beforeTenMinutes = controlPlane.Reports.Count;
        time.Advance(TimeSpan.FromSeconds(1));
        await WaitFor(() => controlPlane.Reports.Count >= 2);
        await reporter.StopAsync(CancellationToken.None);

        beforeTenMinutes.ShouldBe(1);
        controlPlane.Reports.Select(r => r.Provider).ShouldBe(["Gemini", "Gemini"]);
        logger.Entries.ShouldHaveSingleItem().Message.ShouldBe(
            "The worker could not report its readiness: Scout could not reach its control plane.");
    }

    [Fact]
    public async Task Without_A_Usable_Ffmpeg_The_Report_Says_Videos_Cannot_Be_Assembled_And_Why()
    {
        var token = Path.Combine(_folder, "youtube-token.json");
        File.WriteAllText(token, "{}");
        var runner = new FakeProcessRunner { EncoderList = " ------\n V....D libx264  H.264\n A....D aac  AAC\n" };

        var readiness = await Reporter(new MediaCapabilities(true, true, "OpenAI"), tokenFile: token, runner: runner).CurrentAsync(CancellationToken.None);

        readiness.FfmpegAvailable.ShouldBeFalse();
        readiness.Notes.ShouldBe(["The ffmpeg Scout found has no H.264 or VP9 encoder it can use, so videos cannot be assembled."]);
    }

    [Fact]
    public async Task A_voice_with_a_sample_on_disk_names_it()
    {
        var samples = Directory.CreateDirectory(Path.Combine(_folder, "voice-samples")).FullName;
        File.WriteAllBytes(Path.Combine(samples, "piper-en_GB-cori-medium.wav"), new byte[2048]);
        var controlPlane = new RecordingControlPlane { VoiceSamplesDirectory = samples };
        var media = new MediaCapabilities(true, true, "OpenAI") { SpeechVoices = ["nova"] };

        var readiness = await Reporter(media, controlPlane, piper: Bundled()).CurrentAsync(CancellationToken.None);

        readiness.Voices.Select(v => (v.Id, v.SampleFile)).ShouldBe([
            ("OpenAI:nova", (string?)null),
            ("piper:en_GB-aru-medium#0", null),
            ("piper:en_GB-aru-medium#1", null),
            ("piper:en_GB-cori-medium", "piper-en_GB-cori-medium.wav"),
        ]);
    }

    [Fact]
    public async Task A_samples_folder_the_control_plane_refuses_leaves_the_voices_without_samples_and_the_report_still_goes_out()
    {
        var samples = Directory.CreateDirectory(Path.Combine(_folder, "voice-samples")).FullName;
        File.WriteAllBytes(Path.Combine(samples, "piper-en_GB-cori-medium.wav"), new byte[2048]);
        var controlPlane = new RecordingControlPlane
        {
            VoiceSamplesDirectory = samples,
            VoiceSamplesFailure = new InvalidOperationException(RecordingControlPlane.NotGranted),
        };
        using var reporter = Reporter(new MediaCapabilities(false, false, "Gemini"), controlPlane, piper: Bundled());

        await reporter.StartAsync(CancellationToken.None);
        await WaitFor(() => controlPlane.Reports.Count == 1);
        await reporter.StopAsync(CancellationToken.None);

        var report = controlPlane.Reports.ShouldHaveSingleItem();
        report.Voices.Select(v => v.Id).ShouldBe(["piper:en_GB-aru-medium#0", "piper:en_GB-aru-medium#1", "piper:en_GB-cori-medium"]);
        report.Voices.ShouldAllBe(v => v.SampleFile == null);
    }

    [Fact]
    public async Task Transcription_Is_Available_Only_With_A_Speech_To_Text_Model()
    {
        var media = new MediaCapabilities(true, true, "OpenAI");

        var withModel = await Reporter(media, transcriber: new ScriptedTranscriber()).CurrentAsync(CancellationToken.None);
        var without = await Reporter(media).CurrentAsync(CancellationToken.None);

        withModel.TranscriptionAvailable.ShouldBeTrue();
        without.TranscriptionAvailable.ShouldBeFalse();
    }

    [Fact]
    public async Task Lip_Sync_Is_Available_Only_With_Rhubarb_And_The_Nine_Mouths_And_The_Notes_Name_What_Is_Missing()
    {
        var media = new MediaCapabilities(true, true, "OpenAI");
        var (rhubarb, mouths) = Given.MouthShapes(_folder);
        var runner = new FakeProcessRunner
        {
            Answer = (program, _) => program == "/missing/rhubarb" ? throw new InvalidOperationException(ProcessRunner.NotStarted(program)) : null,
        };
        Task<OperatorReadiness> With(string program, string folder)
            => Reporter(media, lipSync: Given.LipSync(runner, program, folder)).CurrentAsync(CancellationToken.None);
        string[] lipNotes = ["Lip sync: the rhubarb program was not found.", "Lip sync: the mouth shapes were not found."];

        var bundled = await With(rhubarb, mouths);
        var noProgram = await With("/missing/rhubarb", mouths);
        File.Delete(Path.Combine(mouths, "H.png"));
        var noShapes = await With(rhubarb, mouths);
        var neither = await With(string.Empty, Path.Combine(_folder, "nowhere"));
        var unbundled = await With(string.Empty, string.Empty);

        (bundled.LipSyncAvailable, noProgram.LipSyncAvailable, noShapes.LipSyncAvailable, neither.LipSyncAvailable, unbundled.LipSyncAvailable)
            .ShouldBe((true, false, false, false, false));
        bundled.Notes.ShouldNotContain(n => n.StartsWith("Lip sync", StringComparison.Ordinal));
        noProgram.Notes.Where(lipNotes.Contains).ShouldBe(["Lip sync: the rhubarb program was not found."]);
        noShapes.Notes.Where(lipNotes.Contains).ShouldBe(["Lip sync: the mouth shapes were not found."]);
        neither.Notes.Where(lipNotes.Contains).ShouldBe(lipNotes);
        unbundled.Notes.ShouldNotContain(n => n.StartsWith("Lip sync", StringComparison.Ordinal));
        runner.Calls.Where(c => c.Arguments.SequenceEqual(["--version"])).Select(c => c.Program).ShouldBe([rhubarb, "/missing/rhubarb", rhubarb]);
    }

    [Fact]
    public async Task A_Rhubarb_That_Did_Not_Answer_Is_Asked_Again_After_Two_Minutes_And_One_That_Did_Is_Kept()
    {
        var (rhubarb, mouths) = Given.MouthShapes(_folder);
        var time = new FakeTimeProvider(Given.Now);
        var answers = false;
        var runner = new FakeProcessRunner { Answer = (_, _) => new ProcessResult(answers ? 0 : 1, string.Empty, string.Empty) };
        var lipSync = Given.LipSync(runner, rhubarb, mouths, time);

        var first = await lipSync.ProbeAsync(CancellationToken.None);
        answers = true;
        time.Advance(TimeSpan.FromSeconds(119));
        var soon = await lipSync.ProbeAsync(CancellationToken.None);
        time.Advance(TimeSpan.FromSeconds(1));
        var later = await lipSync.ProbeAsync(CancellationToken.None);
        answers = false;
        var kept = await lipSync.ProbeAsync(CancellationToken.None);

        (first.Available, soon.Available, later.Available, kept.Available).ShouldBe((false, false, true, true));
        runner.Calls.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Face_Detection_Pictures_Left_And_Built_In_Presenters_Are_Reported_With_A_Note_For_What_Is_Missing()
    {
        var media = new MediaCapabilities(true, true, "OpenAI");
        var kernel = Given.Media(images: Given.Images());
        var visuals = new VisualGenerator(kernel, Given.Capabilities(kernel), new FakeTimeProvider(Given.Now));
        await visuals.PictureAsync("one", _folder, "one.png", CancellationToken.None);
        await visuals.PictureAsync("two", _folder, "two.png", CancellationToken.None);
        var folder = BuiltInPresentersTests.Folder(_folder);
        var models = Directory.CreateDirectory(Path.Combine(_folder, "face")).FullName;
        File.WriteAllBytes(Path.Combine(models, FaceMouth.DetectorFile), [1]);
        File.WriteAllBytes(Path.Combine(models, FaceMouth.MeshFile), [1]);
        FaceMouth Faces(string path, IOnnxRunner onnx)
            => new(Given.Toolset(new FakeProcessRunner()), new MediaProbe(new FakeProcessRunner()), onnx,
                Options.Create(new OperatorMediaOptions { FaceModelsPath = path }), new RecordingLogger<FaceMouth>());
        var loads = new FaceMouthTests.FakeOnnx { Answer = _ => new Dictionary<string, float[]>() };

        var ready = await Reporter(media, faces: Faces(models, loads), presenters: BuiltInPresentersTests.Presenters(folder), visuals: visuals).CurrentAsync(CancellationToken.None);
        var missing = await Reporter(media, faces: Faces(Path.Combine(_folder, "nowhere"), loads), presenters: BuiltInPresentersTests.Presenters(Path.Combine(_folder, "nowhere")))
            .CurrentAsync(CancellationToken.None);
        var unbundled = await Reporter(media, faces: Faces(string.Empty, loads), presenters: BuiltInPresentersTests.Presenters(string.Empty)).CurrentAsync(CancellationToken.None);

        (ready.FaceDetectionAvailable, ready.PicturesLeftToday).ShouldBe((true, 38));
        ready.BuiltInPresenters.ShouldBe([
            new OperatorBuiltInPresenter("ada", "Ada, the baker"), new OperatorBuiltInPresenter("bo", "Bo, the gardener"),
            new OperatorBuiltInPresenter("cy", "Cy, the engineer"), new OperatorBuiltInPresenter("di", "Di, the teacher"),
        ]);
        ready.Notes.ShouldNotContain(n => n.StartsWith("Face detection", StringComparison.Ordinal) || n.StartsWith("Presenters", StringComparison.Ordinal));
        (missing.FaceDetectionAvailable, missing.BuiltInPresenters.Count).ShouldBe((false, 0));
        missing.Notes.ShouldContain("Face detection: the models were not found.");
        missing.Notes.ShouldContain("Presenters: the built-in presenters were not found.");
        unbundled.Notes.ShouldNotContain(n => n.StartsWith("Face detection", StringComparison.Ordinal) || n.StartsWith("Presenters", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Face_Detection_Is_Not_Available_Without_A_Usable_Ffmpeg_Even_With_The_Models()
    {
        var models = Directory.CreateDirectory(Path.Combine(_folder, "face")).FullName;
        File.WriteAllBytes(Path.Combine(models, FaceMouth.DetectorFile), [1]);
        File.WriteAllBytes(Path.Combine(models, FaceMouth.MeshFile), [1]);
        var faces = new FaceMouth(Given.Toolset(new FakeProcessRunner()), new MediaProbe(new FakeProcessRunner()), new FaceMouthTests.FakeOnnx { Answer = _ => new Dictionary<string, float[]>() },
            Options.Create(new OperatorMediaOptions { FaceModelsPath = models }), new RecordingLogger<FaceMouth>());
        var runner = new FakeProcessRunner { EncoderList = " ------\n V....D libx264  H.264\n A....D aac  AAC\n" };

        var readiness = await Reporter(new MediaCapabilities(true, true, "OpenAI"), runner: runner, faces: faces).CurrentAsync(CancellationToken.None);

        (readiness.FfmpegAvailable, readiness.FaceDetectionAvailable).ShouldBe((false, false));
        readiness.Notes.ShouldNotContain("Face detection: the models were not found.");
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(10);
        }

        condition().ShouldBeTrue();
    }
}
