#pragma warning disable SKEXP0001
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.TextToImage;
using Scout.Conversation.Abstractions;
using Scout.Operator.Tests.Fakes;

namespace Scout.Operator.Tests;

public sealed class PresenterStageTests : IDisposable
{
    private const string Tail = "No text, no letters, no logos, no trademarks, no real person's likeness and no public figure.";

    private const string Style =
        "flat 2D cartoon illustration, front-facing, looking at the viewer, mouth closed, neutral expression, head and shoulders, on a solid bright green background (#00FF00)";

    private static readonly OperatorMouthBox Found = new(0.44, 0.58, 0.12, 0.06);

    private readonly string _root = Directory.CreateTempSubdirectory("scout-presenter-").FullName;

    public PresenterStageTests() => Media = Directory.CreateDirectory(Path.Combine(_root, "media")).FullName;

    private string Media { get; }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static OperatorProjectState Presenting(string request = "first", string prompt = "", params OperatorPresenterVersion[] versions)
        => Given.Project(status: "presenting") with
        {
            Format = "animated_character",
            PresenterRequest = request,
            PresenterRequestPrompt = prompt,
            PresenterReturnStatus = "script_ready",
            PresenterVersions = versions,
            Script = new OperatorScript("Bread in ten minutes? Yes, really.", [new OperatorScriptSection("Starter", "Feed it daily.")], "Subscribe", ["A"], "How to bake.", [], []),
        };

    private static OperatorPresenterVersion Version(int n, string prompt, string feedback = "")
        => new($"presenter-00000000000{n}.png", prompt, feedback, $"presenter-preview-{n}.mp4", Found, "2026-11-02T10:00:00Z", "generated");

    private RecordingControlPlane Holding(OperatorProjectState project, Func<OperatorProjectState, OperatorProjectState>? keeps = null)
    {
        var controlPlane = new RecordingControlPlane { MediaDirectory = Media, Keeps = keeps ?? (p => p) };
        controlPlane.Store(project);
        return controlPlane;
    }

    private static FakeProcessRunner Runner(Func<string, IReadOnlyList<string>, ProcessResult?>? answer = null)
        => new()
        {
            Answer = (program, arguments) => arguments.Count >= 2 && arguments[^2] == "-i" && arguments[^1].EndsWith(".png", StringComparison.Ordinal)
                ? new ProcessResult(1, string.Empty, $"Input #0, png_pipe, from '{arguments[^1]}':\n  Stream #0:0: Video: png, rgba(pc), 1024x1024, 25 tbr\n")
                : answer?.Invoke(program, arguments),
        };

    private (PresenterStage Stage, FakeProcessRunner Runner) Stage(
        RecordingControlPlane controlPlane, Kernel kernel, IFaceMouth? faces = null, FakeProcessRunner? runner = null, bool presenters = true, VisualGenerator? visuals = null)
    {
        var processes = runner ?? Runner();
        var capabilities = Given.Capabilities(kernel);
        var clock = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(Given.Now);
        var (rhubarb, mouths) = Given.MouthShapes(_root);
        var stage = Given.Presenter(
            controlPlane,
            processes,
            visuals ?? new VisualGenerator(kernel, capabilities, clock),
            new NarrationGenerator(kernel, capabilities, Given.Piper(new FakeProcessRunner())),
            faces ?? new FoundMouth(Found),
            presenters ? BuiltInPresentersTests.Folder(_root) : string.Empty,
            clock,
            Given.LipSync(processes, rhubarb, mouths));
        return (stage, processes);
    }

    private static string Prompt(ITextToImageService images)
        => images.ReceivedCalls().ShouldHaveSingleItem().GetArguments()[0].ShouldBeOfType<TextContent>().Text!;

    private static Task<OperatorProjectState> Stored(RecordingControlPlane controlPlane) => controlPlane.GetProjectAsync("p1", CancellationToken.None);

    [Fact]
    public async Task The_First_Presenter_Is_Made_From_The_Topic_Alone_With_The_Safety_Tail_And_Its_Preview()
    {
        var controlPlane = Holding(Presenting());
        var images = Given.Images();
        var speech = Given.Speech();
        var (stage, runner) = Stage(controlPlane, Given.Media(speech, images));

        var finished = await stage.RunAsync("p1", CancellationToken.None);

        finished.ShouldBeTrue();
        Prompt(images).ShouldBe($"sourdough. {Style}. {Tail}");
        speech.ReceivedCalls().ShouldHaveSingleItem().GetArguments()[0].ShouldBe("Bread in ten minutes?");
        var saved = await Stored(controlPlane);
        var version = saved.PresenterVersions.ShouldHaveSingleItem();
        version.File.ShouldMatch("^presenter-[0-9a-f]{12}\\.png$");
        (version.Prompt, version.Feedback, version.PreviewFile, version.MouthBox, version.MadeAt, version.Source).ShouldBe(
            ($"sourdough. {Style}.", string.Empty, "presenter-preview-1.mp4", Found, "2026-11-03T12:00:00Z", "generated"));
        (saved.Status, saved.PresenterRequest, saved.PresenterRequestPrompt, saved.Error).ShouldBe(("script_ready", string.Empty, string.Empty, (string?)null));
        (saved.PresenterFile, saved.MouthBox, saved.PresenterPreviewFile, saved.PresenterSource).ShouldBe((version.File, Found, "presenter-preview-1.mp4", "generated"));
        File.Exists(Path.Combine(Media, version.File)).ShouldBeTrue();
        File.Exists(Path.Combine(Media, "presenter-preview-1.mp3")).ShouldBeTrue();
        File.Exists(Path.Combine(Media, "presenter-preview-1.mp4")).ShouldBeTrue();
        Directory.GetFiles(Media, "mouth-cues-preview-*").ShouldBeEmpty();
        var preview = runner.Renders.Single(r => r.Contains("-filter_complex"));
        preview.ShouldContain("color=c=0x14181f:s=1280x720:r=30");
        preview.ShouldContain(Path.Combine(Media, "presenter-preview-1.mp3"));
        preview[preview.ToList().IndexOf("-filter_complex") + 1].ShouldContain(
            "[1:v]chromakey=0x00FF00:0.1:0.2,scale=324:324,setsar=1[face];[bg][face]overlay=918:374[body];[2:v]scale=39:19[mouth];[body][mouth]overlay=1061:562", Case.Sensitive);
        controlPlane.Saves.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task The_First_Presenter_Prompt_Is_The_Direction_Then_The_Presenter_Description_Then_The_Style()
    {
        var controlPlane = Holding(Presenting() with { Direction = "Warm kitchen light.", Presenter = "A cheerful baker in a flour-dusted apron." });
        var images = Given.Images();

        await Stage(controlPlane, Given.Media(Given.Speech(), images)).Stage.RunAsync("p1", CancellationToken.None);

        Prompt(images).ShouldBe($"Warm kitchen light. A cheerful baker in a flour-dusted apron. {Style}. {Tail}");
        (await Stored(controlPlane)).PresenterVersions.ShouldHaveSingleItem().Prompt
            .ShouldBe($"Warm kitchen light. A cheerful baker in a flour-dusted apron. {Style}.");
    }

    [Fact]
    public async Task The_Preview_Plays_Over_The_First_Scenes_Picture()
    {
        File.WriteAllBytes(Path.Combine(Media, "scene-s1.png"), [1, 2]);
        File.WriteAllBytes(Path.Combine(Media, "scene-s2.png"), [3, 4]);
        var controlPlane = Holding(Presenting() with
        {
            Scenes =
            [
                Given.Scene("s2", 2, status: "ready", visualSource: "generated", visualFile: "scene-s2.png"),
                Given.Scene("s1", 1, status: "ready", visualSource: "generated", visualFile: "scene-s1.png"),
            ],
        });
        var (stage, runner) = Stage(controlPlane, Given.Media(Given.Speech(), Given.Images()));

        await stage.RunAsync("p1", CancellationToken.None);

        var preview = runner.Renders.Single(r => r.Contains("-filter_complex"));
        preview.Take(5).ShouldBe(["-hide_banner", "-nostdin", "-y", "-i", Path.Combine(Media, "scene-s1.png")]);
        preview.ShouldNotContain(Path.Combine(Media, "scene-s2.png"));
        preview[preview.ToList().IndexOf("-filter_complex") + 1].ShouldStartWith(
            "[0:v]scale=2560:1440:force_original_aspect_ratio=decrease,pad=2560:1440:(ow-iw)/2:(oh-ih)/2:color=black,setsar=1,zoompan=", Case.Sensitive);
        (await Stored(controlPlane)).PresenterPreviewFile.ShouldBe("presenter-preview-1.mp4");
    }

    [Fact]
    public async Task Feedback_Is_Appended_To_The_Previous_Prompt_In_Order()
    {
        var first = $"sourdough. {Style}.";
        var second = $"{first} Change: make the apron blue.";
        var controlPlane = Holding(Presenting("Add a white chef's hat", "", Version(1, first), Version(2, second, "make the apron blue")));
        var images = Given.Images();
        var (stage, _) = Stage(controlPlane, Given.Media(Given.Speech(), images));

        await stage.RunAsync("p1", CancellationToken.None);

        Prompt(images).ShouldBe($"{first} Change: make the apron blue. Change: Add a white chef's hat. {Tail}");
        var saved = await Stored(controlPlane);
        saved.PresenterVersions.Count.ShouldBe(3);
        (saved.PresenterVersions[2].Prompt, saved.PresenterVersions[2].Feedback, saved.PresenterVersions[2].PreviewFile).ShouldBe(
            ($"{second} Change: Add a white chef's hat.", "Add a white chef's hat", "presenter-preview-3.mp4"));
        saved.PresenterVersions.Take(2).ShouldBe([Version(1, first), Version(2, second, "make the apron blue")]);
    }

    [Fact]
    public async Task Feedback_Builds_On_The_Current_Version_Not_The_Newest()
    {
        var versions = new[] { Version(1, "a baker with a red apron"), Version(2, "a baker with a blue apron"), Version(3, "a baker with a green apron") };
        var project = Presenting("Add a hat", "", versions) with { PresenterFile = versions[0].File };
        var images = Given.Images();
        var (stage, _) = Stage(Holding(project), Given.Media(Given.Speech(), images));

        await stage.RunAsync("p1", CancellationToken.None);

        Prompt(images).ShouldBe($"a baker with a red apron Change: Add a hat. {Tail}");
    }

    [Fact]
    public async Task An_Edited_Prompt_Is_Used_Verbatim_Plus_The_Safety_Tail()
    {
        var edited = Holding(Presenting(prompt: "A robot chef with a copper whisk, in a tiny kitchen", versions: Version(1, $"sourdough. {Style}.")));
        var withFeedback = Holding(Presenting("Smile more", "A robot chef with a copper whisk"));
        var images = Given.Images();
        var otherImages = Given.Images();

        await Stage(edited, Given.Media(Given.Speech(), images)).Stage.RunAsync("p1", CancellationToken.None);
        await Stage(withFeedback, Given.Media(Given.Speech(), otherImages)).Stage.RunAsync("p1", CancellationToken.None);

        Prompt(images).ShouldBe($"A robot chef with a copper whisk, in a tiny kitchen {Tail}");
        (await Stored(edited)).PresenterVersions[^1].Prompt.ShouldBe("A robot chef with a copper whisk, in a tiny kitchen");
        Prompt(otherImages).ShouldBe($"A robot chef with a copper whisk Change: Smile more. {Tail}");
    }

    [Theory]
    [InlineData("prompt", "A robot chef with a copper whisk")]
    [InlineData("another", "")]
    public async Task The_Control_Plane_Sentinels_Carry_No_Feedback(string request, string prompt)
    {
        var controlPlane = Holding(Presenting(request, prompt));
        var images = Given.Images();

        await Stage(controlPlane, Given.Media(Given.Speech(), images)).Stage.RunAsync("p1", CancellationToken.None);

        var composed = prompt.Length > 0 ? prompt : $"sourdough. {Style}.";
        Prompt(images).ShouldBe($"{composed} {Tail}");
        var version = (await Stored(controlPlane)).PresenterVersions.ShouldHaveSingleItem();
        (version.Prompt, version.Feedback).ShouldBe((composed, string.Empty));
    }

    [Fact]
    public async Task A_First_Request_On_The_Owners_Picture_Keeps_It_And_Finds_Its_Mouth()
    {
        var file = "owner-presenter-0123456789ab.png";
        File.WriteAllBytes(Path.Combine(Media, file), [1]);
        var controlPlane = Holding(Presenting() with { PresenterSource = "owner", PresenterFile = file });
        var images = Given.Images();
        var faces = new FoundMouth(Found);

        await Stage(controlPlane, Given.Media(Given.Speech(), images), faces).Stage.RunAsync("p1", CancellationToken.None);

        var version = (await Stored(controlPlane)).PresenterVersions.ShouldHaveSingleItem();
        (version.File, version.Source, version.MouthBox).ShouldBe((file, "owner", Found));
        images.ReceivedCalls().ShouldBeEmpty();
        File.Exists(Path.Combine(Media, file)).ShouldBeTrue();
    }

    [Theory]
    [InlineData("builtin:di", "di", 0.43, 0.63, 0.15, 0.07)]
    [InlineData("builtin:zed", "ada", 0.42, 0.61, 0.16, 0.08)]
    public async Task A_Built_In_Presenter_Is_Taken_By_Id_With_Its_Own_Mouth_Box_And_No_Picture_Request(
        string request, string id, double x, double y, double width, double height)
    {
        var controlPlane = Holding(Presenting(request));
        var images = Given.Images();
        var faces = new FoundMouth(Found);

        await Stage(controlPlane, Given.Media(Given.Speech(), images), faces).Stage.RunAsync("p1", CancellationToken.None);

        var version = (await Stored(controlPlane)).PresenterVersions.ShouldHaveSingleItem();
        (version.File, version.Source, version.Prompt, version.Feedback).ShouldBe(($"presenter-builtin-{id}.png", "builtin", string.Empty, request));
        version.MouthBox.ShouldBe(new OperatorMouthBox(x, y, width, height));
        images.ReceivedCalls().ShouldBeEmpty();
        faces.Pictures.ShouldBeEmpty();
        File.Exists(Path.Combine(Media, $"presenter-builtin-{id}.png")).ShouldBeTrue();
    }

    [Theory]
    [InlineData("no image service", null)]
    [InlineData("the request fails", null)]
    [InlineData("the day is spent", null)]
    [InlineData("no face", "No face was found on the presenter picture; Scout's own presenter Ada, the baker is used. Mark the mouth or ask for another picture.")]
    public async Task Without_An_Image_Service_Or_A_Face_The_Built_In_Presenter_Is_Used_With_Its_Box_And_The_Note(string how, string? note)
    {
        var controlPlane = Holding(Presenting());
        var images = Substitute.For<ITextToImageService>();
        images.GetImageContentsAsync(default!, default, default, default).ReturnsForAnyArgs<IReadOnlyList<ImageContent>>(_ => how == "the request fails"
            ? throw new HttpRequestException("503 Service Unavailable")
            : [new ImageContent(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, "image/png")]);
        var kernel = Given.Media(Given.Speech(), how == "no image service" ? null : images);
        var visuals = new VisualGenerator(kernel, Given.Capabilities(kernel), new Microsoft.Extensions.Time.Testing.FakeTimeProvider(Given.Now));
        for (var i = 0; how == "the day is spent" && i < VisualGenerator.PerDay; i++)
        {
            await visuals.PictureAsync("filler", Media, "filler.png", CancellationToken.None);
        }

        var (stage, _) = Stage(controlPlane, kernel, new FoundMouth(how == "no face" ? null : Found), visuals: visuals);

        await stage.RunAsync("p1", CancellationToken.None);

        var saved = await Stored(controlPlane);
        var version = saved.PresenterVersions.ShouldHaveSingleItem();
        (version.File, version.Source, version.MouthBox, version.Prompt).ShouldBe(
            ("presenter-builtin-ada.png", "builtin", new OperatorMouthBox(0.42, 0.61, 0.16, 0.08), $"sourdough. {Style}."));
        (saved.Status, saved.Error, saved.PresenterSource).ShouldBe(("script_ready", note, "builtin"));
        Directory.GetFiles(Media, "presenter-*.png").Select(Path.GetFileName).ShouldBe(["presenter-builtin-ada.png"]);
    }

    [Fact]
    public async Task A_Preview_That_Fails_To_Render_Keeps_The_Version_Without_A_Preview_And_Notes_It()
    {
        var controlPlane = Holding(Presenting());
        var runner = Runner((_, arguments) => arguments.Contains("-filter_complex")
            ? new ProcessResult(1, string.Empty, "Error initializing filter 'chromakey'\n")
            : null);

        await Stage(controlPlane, Given.Media(Given.Speech(), Given.Images()), runner: runner).Stage.RunAsync("p1", CancellationToken.None);

        var saved = await Stored(controlPlane);
        var version = saved.PresenterVersions.ShouldHaveSingleItem();
        (version.PreviewFile, saved.PresenterPreviewFile, version.MouthBox).ShouldBe(((string?)null, (string?)null, Found));
        saved.Error.ShouldBe("Scout could not make the presenter preview: Scout could not render the presenter preview (ffmpeg: Error initializing filter 'chromakey')");
        Directory.GetFiles(Media, "presenter-preview-*").ShouldBeEmpty();
    }

    [Fact]
    public async Task A_Preview_Whose_Mouth_Track_Cannot_Be_Written_Leaves_No_Mouth_Cues_File()
    {
        Directory.CreateDirectory(Path.Combine(Media, MouthTrack.FileName("preview-1")));
        var controlPlane = Holding(Presenting());

        await Stage(controlPlane, Given.Media(Given.Speech(), Given.Images())).Stage.RunAsync("p1", CancellationToken.None);

        Directory.GetFiles(Media, "mouth-cues-preview-*").ShouldBeEmpty();
    }

    [Fact]
    public async Task The_Worker_Removes_Only_The_Preview_Audio_Of_A_Version_The_Control_Plane_Drops()
    {
        var builtIn = new OperatorPresenterVersion("presenter-builtin-ada.png", string.Empty, "builtin:ada", "presenter-preview-1.mp4", Found, "2026-11-02T10:00:00Z", "builtin");
        var again = builtIn with { PreviewFile = "presenter-preview-2.mp4" };
        foreach (var file in new[] { "presenter-builtin-ada.png", "presenter-preview-1.mp4", "presenter-preview-1.wav", "presenter-preview-1.mp3", "presenter-preview-2.mp4", "presenter-preview-2.mp3" })
        {
            File.WriteAllBytes(Path.Combine(Media, file), [1]);
        }

        var controlPlane = Holding(
            Presenting("builtin:ada", "", builtIn, again),
            keeps: p => p with { PresenterVersions = [.. p.PresenterVersions.Skip(1)] });

        await Stage(controlPlane, Given.Media(Given.Speech(), Given.Images())).Stage.RunAsync("p1", CancellationToken.None);

        (await Stored(controlPlane)).PresenterVersions.Select(v => v.PreviewFile).ShouldBe(["presenter-preview-2.mp4", "presenter-preview-3.mp4"]);
        Directory.GetFiles(Media, "presenter-*").Select(Path.GetFileName).Order(StringComparer.Ordinal).ShouldBe([
            "presenter-builtin-ada.png", "presenter-preview-1.mp4", "presenter-preview-2.mp3", "presenter-preview-2.mp4", "presenter-preview-3.mp3", "presenter-preview-3.mp4",
        ]);
    }

    [Fact]
    public async Task A_Project_No_Longer_Presenting_Is_Left_As_It_Is()
    {
        var controlPlane = Holding(Presenting() with { Status = "producing" });
        var images = Given.Images();

        var finished = await Stage(controlPlane, Given.Media(Given.Speech(), images)).Stage.RunAsync("p1", CancellationToken.None);

        finished.ShouldBeFalse();
        controlPlane.Saves.ShouldBeEmpty();
        images.ReceivedCalls().ShouldBeEmpty();
    }

    [Fact]
    public async Task A_Presenter_Job_That_Fails_Gives_The_Project_Its_Status_Back_With_The_Reason_And_Posts_Nothing()
    {
        var controlPlane = new RecordingControlPlane { MediaDirectoryFailure = new InvalidOperationException("The media folder is not available.") };
        controlPlane.Store(Presenting());
        var notifier = Substitute.For<IOriginNotifier>();
        var images = Substitute.For<ITextToImageService>();
        var kernel = Given.Media(Given.Speech(), images);
        var pipeline = Given.Pipeline(controlPlane, Given.YouTube(), ScriptedChat.Answering("{}"), notifier, kernel, presenters: Path.Combine(_root, "presenters"));

        await pipeline.RunAsync(new QueuedProject(Presenting(), "discord:3333:42"), CancellationToken.None);

        var saved = await Stored(controlPlane);
        (saved.Status, saved.Error, saved.PresenterRequest).ShouldBe(("script_ready", "The media folder is not available.", string.Empty));
        notifier.ReceivedCalls().ShouldBeEmpty();
    }
}
