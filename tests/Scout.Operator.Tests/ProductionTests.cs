#pragma warning disable SKEXP0001
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.TextToAudio;
using Scout.Conversation.Abstractions;
using Scout.Operator.Tests.Fakes;

namespace Scout.Operator.Tests;

public sealed class ProductionTests : IDisposable
{
    private const string Origin = "discord:3333:42";

    private readonly string _media = Directory.CreateTempSubdirectory("scout-production-").FullName;

    public void Dispose() => Directory.Delete(_media, recursive: true);

    private RecordingControlPlane Holding(OperatorProjectState project, Exception? mediaDirectoryFailure = null)
    {
        var controlPlane = new RecordingControlPlane { MediaDirectory = _media, MediaDirectoryFailure = mediaDirectoryFailure };
        controlPlane.Store(project);
        return controlPlane;
    }

    private static IOriginNotifier Notifier()
    {
        var notifier = Substitute.For<IOriginNotifier>();
        notifier.PostAsync(default!, default!, default).ReturnsForAnyArgs(true);
        return notifier;
    }

    private static List<string> Posts(IOriginNotifier notifier)
        => notifier.ReceivedCalls().Select(c => (string)c.GetArguments()[1]!).ToList();

    private static Task Run(RecordingControlPlane controlPlane, OperatorProjectState project, Kernel media, IOriginNotifier? notifier = null, ScriptedChat? chat = null)
        => Given.Pipeline(controlPlane, Given.YouTube(), chat ?? ScriptedChat.Answering(Given.Prompts(3)), notifier ?? Notifier(), media)
            .RunAsync(new QueuedProject(project, Origin), CancellationToken.None);

    private static Task<OperatorProjectState> Stored(RecordingControlPlane controlPlane)
        => controlPlane.GetProjectAsync("p1", CancellationToken.None);

    [Fact]
    public async Task A_Producing_Project_Without_Scenes_Is_Planned_Then_Every_Scene_Gets_Narration_And_A_Picture_Saved_One_By_One_And_Ends_Media_Ready()
    {
        var project = Given.Producing();
        var controlPlane = Holding(project);
        var notifier = Notifier();

        await Run(controlPlane, project, Given.Media(Given.Speech(), Given.Images()), notifier);

        var saved = await Stored(controlPlane);
        saved.Status.ShouldBe("media_ready");
        saved.Scenes.Select(s => (s.Id, s.Status, s.AudioSource, s.AudioFile, s.VisualSource, s.VisualFile)).ShouldBe([
            ("s1", "ready", "generated", "scene-s1.mp3", "generated", "scene-s1.png"),
            ("s2", "ready", "generated", "scene-s2.mp3", "generated", "scene-s2.png"),
            ("s3", "ready", "generated", "scene-s3.mp3", "generated", "scene-s3.png"),
        ]);
        controlPlane.Saves.Select(s => (s.Project.Status, s.Project.Scenes.Count(c => c.Status == "ready"))).ShouldBe([
            ("producing", 0), ("producing", 1), ("producing", 2), ("producing", 3), ("media_ready", 3),
        ]);
        Directory.GetFiles(_media).Select(Path.GetFileName).Order().ShouldBe([
            "scene-s1.mp3", "scene-s1.png", "scene-s2.mp3", "scene-s2.png", "scene-s3.mp3", "scene-s3.png",
        ]);
        Posts(notifier).ShouldBe([
            "Producing narration and visuals for sourdough...",
            "Media ready for sourdough: review the scenes in Scout Desktop, Production.",
        ]);
    }

    [Fact]
    public async Task A_Project_Is_Planned_In_The_Owners_Direction_And_Its_Format_Direction_And_Motion_Go_Back_As_Read()
    {
        var project = Given.Producing() with { Direction = "Bold comic panels, it's: loud.", Motion = "still", Format = "generated_pictures" };
        var controlPlane = Holding(project);
        var chat = ScriptedChat.Answering(Given.Prompts(3));

        await Run(controlPlane, project, Given.Media(Given.Speech(), Given.Images()), chat: chat);

        chat.Requests.ShouldHaveSingleItem().Single(m => m.Role == Microsoft.SemanticKernel.ChatCompletion.AuthorRole.System).Text
            .ShouldContain("Direction the owner gave for this video: Bold comic panels, it's: loud.", Case.Sensitive);
        controlPlane.Saves.ShouldNotBeEmpty();
        controlPlane.Saves.ShouldAllBe(s =>
            s.Project.Direction == "Bold comic panels, it's: loud." && s.Project.Motion == "still" && s.Project.Format == "generated_pictures");
    }

    [Fact]
    public async Task An_Edited_Scene_Is_Narrated_Again_And_Keeps_Its_Picture()
    {
        File.WriteAllBytes(Path.Combine(_media, "scene-s1.png"), [7, 7]);
        File.WriteAllBytes(Path.Combine(_media, "scene-s2.mp3"), [1]);
        File.WriteAllBytes(Path.Combine(_media, "scene-s2.png"), [1]);
        var (piper, voices) = Given.BundledVoices(Directory.CreateDirectory(Path.Combine(_media, "bundle")).FullName);
        var edited = Given.Scene("s1", 1, "The owner's new words.", visualSource: "generated", visualFile: "scene-s1.png");
        var untouched = Given.Scene("s2", 2, "Bake it hot.", "ready", audioSource: "generated", visualSource: "generated", audioFile: "scene-s2.mp3", visualFile: "scene-s2.png", durationMs: 900);
        var project = Given.Producing(edited, untouched) with { NarrationVoice = "piper:en_GB-aru-medium#0" };
        var controlPlane = Holding(project);
        var runner = new FakeProcessRunner();
        var speech = Given.Speech();
        var images = Given.Images();

        await Given.Pipeline(controlPlane, Given.YouTube(), ScriptedChat.Answering(), Notifier(), Given.Media(speech, images), piper: Given.Piper(runner, piper, voices))
            .RunAsync(new QueuedProject(project, Origin), CancellationToken.None);

        var saved = await Stored(controlPlane);
        saved.Status.ShouldBe("media_ready");
        var scene = saved.Scenes[0];
        (scene.Narration, scene.Status, scene.AudioSource, scene.AudioFile).ShouldBe(("The owner's new words.", "ready", "generated", "scene-s1.wav"));
        (scene.VisualSource, scene.VisualFile).ShouldBe(("generated", "scene-s1.png"));
        saved.Scenes[^1].ShouldBe(untouched);
        runner.Calls.ShouldHaveSingleItem().Arguments[^1].ShouldBe("0");
        runner.Inputs.ShouldHaveSingleItem().ShouldBe("The owner's new words.");
        images.ReceivedCalls().ShouldBeEmpty();
        speech.ReceivedCalls().ShouldBeEmpty();
        File.ReadAllBytes(Path.Combine(_media, "scene-s1.png")).ShouldBe([7, 7]);
    }

    [Fact]
    public async Task An_Own_Pictures_Scene_Without_The_Owners_Picture_Is_A_Text_Card_With_A_Note_And_No_Picture_Is_Made()
    {
        File.WriteAllBytes(Path.Combine(_media, "mine.jpg"), [1]);
        var owners = Given.Scene("s2", 2, "Look at this bend.", visualSource: "owner", visualFile: "mine.jpg");
        var project = Given.Producing(Given.Scene("s1", 1, "Rivers bend."), owners) with { Format = "own_pictures" };
        var controlPlane = Holding(project);
        using var openverse = new OpenverseHttp();
        var images = Given.Images();
        var pipeline = Given.Pipeline(controlPlane, Given.YouTube(), ScriptedChat.Answering(), Notifier(), Given.Media(Given.Speech(), images), openverse: openverse);

        await pipeline.RunAsync(new QueuedProject(project, Origin), CancellationToken.None);
        var first = await Stored(controlPlane);
        controlPlane.Store(first with { Status = "producing" });
        var savesBefore = controlPlane.Saves.Count;
        await pipeline.RunAsync(new QueuedProject(first with { Status = "producing" }, Origin), CancellationToken.None);

        first.Status.ShouldBe("media_ready");
        first.Scenes.Select(s => (s.Id, s.Status, s.VisualSource, s.VisualFile, s.AudioSource)).ShouldBe([
            ("s1", "ready", "text_card", (string?)null, "generated"),
            ("s2", "ready", "owner", (string?)"mine.jpg", "generated"),
        ]);
        first.Scenes[0].Error.ShouldBe("No picture: this video uses your own pictures; attach one to this scene in Scout Desktop, Production. Until then it shows the project background or a text card.");
        first.Scenes[1].Error.ShouldBeNull();
        images.ReceivedCalls().ShouldBeEmpty();
        openverse.Requests.ShouldBeEmpty();
        controlPlane.Saves.Skip(savesBefore).Select(s => s.Project.Status).ShouldBe(["media_ready"]);
        (await Stored(controlPlane)).Scenes.ShouldBe(first.Scenes);
    }

    [Fact]
    public async Task Without_provider_pictures_each_scene_is_saved_with_its_openverse_picture_its_credit_and_its_search_terms()
    {
        var project = Given.Producing();
        var controlPlane = Holding(project);
        using var openverse = new OpenverseHttp();
        var chat = ScriptedChat.Answering("{\"prompts\":[\"Picture 1\",\"Picture 2\",\"Picture 3\"],\"search_terms\":[\"starter jar\",\"bread oven\",\"bread loaf\"]}");

        await Given.Pipeline(controlPlane, Given.YouTube(), chat, Notifier(), Given.Media(Given.Speech()), openverse: openverse)
            .RunAsync(new QueuedProject(project, Origin), CancellationToken.None);

        var saved = await Stored(controlPlane);
        saved.Status.ShouldBe("media_ready");
        saved.Scenes.Select(s => (s.VisualSource, s.VisualFile, s.VisualCredit, s.VisualQuery)).ShouldBe([
            ("stock", (string?)"scene-s1.jpg", (string?)"\"Mountain lake\" by Thomas Nes Myhre is licensed under CC BY 2.0.", "starter jar"),
            ("stock", (string?)"scene-s2.jpg", (string?)"\"Mountain lake\" by Thomas Nes Myhre is licensed under CC BY 2.0.", "bread oven"),
            ("stock", (string?)"scene-s3.jpg", (string?)"\"Mountain lake\" by Thomas Nes Myhre is licensed under CC BY 2.0.", "bread loaf"),
        ]);
        saved.Scenes.ShouldAllBe(s => s.Error == "Picture from Openverse, credited in the video's description.");
        openverse.Requests.Count(r => r.RequestUri!.Host == "api.openverse.org").ShouldBe(3);
    }

    [Fact]
    public async Task A_Resumed_Project_Is_Narrated_In_The_Voice_The_Project_Names()
    {
        var (piper, voices) = Given.BundledVoices(Directory.CreateDirectory(Path.Combine(_media, "bundle")).FullName);
        var project = Given.Producing(Given.Scene("s1", 1)) with { NarrationVoice = "piper:en_GB-aru-medium#0" };
        var controlPlane = new RecordingControlPlane
        {
            MediaDirectory = _media,
            Mandate = Given.Mandate(),
        };
        controlPlane.Store(project);
        var runner = new FakeProcessRunner();
        var speech = Given.Speech();

        await Given.Pipeline(controlPlane, Given.YouTube(), ScriptedChat.Answering(), Notifier(), Given.Media(speech, Given.Images()), piper: Given.Piper(runner, piper, voices))
            .RunAsync(new QueuedProject(project, Origin), CancellationToken.None);

        var scene = (await Stored(controlPlane)).Scenes.ShouldHaveSingleItem();
        (scene.Status, scene.AudioSource, scene.AudioFile).ShouldBe(("ready", "generated", "scene-s1.wav"));
        runner.Calls.ShouldHaveSingleItem().Arguments[^1].ShouldBe("0");
        speech.ReceivedCalls().ShouldBeEmpty();
        File.Exists(Path.Combine(_media, "scene-s1.wav")).ShouldBeTrue();
    }

    [Fact]
    public async Task Without_Speech_Or_Image_Services_Every_Scene_Is_Ready_With_No_Audio_A_Text_Card_An_Estimated_Duration_And_Notes()
    {
        var project = Given.Producing(Given.Scene("s1", 1, "one two three four five six seven eight nine ten"));
        var controlPlane = Holding(project);

        await Run(controlPlane, project, Given.Media());

        var scene = (await Stored(controlPlane)).Scenes.ShouldHaveSingleItem();
        (scene.Status, scene.AudioSource, scene.AudioFile, scene.VisualSource, scene.VisualFile, scene.DurationMs)
            .ShouldBe(("ready", "none", null, "text_card", null, 4000));
        scene.Error.ShouldBe(
            "No narration: the connected AI Source has no speech service in Scout; attach your own audio in Scout Desktop, Production. " +
            "No picture: the connected AI Source has no image service in Scout; the scene shows a text card. Attach a background to the project in Scout Desktop, Production (it sits under every scene without a picture), or connect an AI Source with pictures.");
        (await Stored(controlPlane)).Status.ShouldBe("media_ready");
    }

    [Fact]
    public async Task A_Second_Run_Skips_Ready_Scenes_Whose_Files_Are_On_Disk_And_Remakes_Those_Whose_Files_Are_Missing()
    {
        File.WriteAllBytes(Path.Combine(_media, "scene-s1.mp3"), [1]);
        File.WriteAllBytes(Path.Combine(_media, "scene-s1.png"), [1]);
        File.WriteAllBytes(Path.Combine(_media, "scene-s2.png"), [1]);
        var project = Given.Producing(
            Given.Scene("s1", 1, "Ready and whole.", "ready", audioSource: "generated", visualSource: "generated", audioFile: "scene-s1.mp3", visualFile: "scene-s1.png", durationMs: 1200),
            Given.Scene("s2", 2, "Ready but its audio is gone.", "ready", audioSource: "generated", visualSource: "generated", audioFile: "scene-s2.mp3", visualFile: "scene-s2.png", durationMs: 1200),
            Given.Scene("s3", 3, "Cut short last time."));
        var controlPlane = Holding(project);
        var speech = Given.Speech();

        await Run(controlPlane, project, Given.Media(speech, Given.Images()));

        await speech.Received(1).GetAudioContentsAsync("Ready but its audio is gone.", Arg.Any<PromptExecutionSettings?>(), Arg.Any<Kernel?>(), Arg.Any<CancellationToken>());
        await speech.Received(1).GetAudioContentsAsync("Cut short last time.", Arg.Any<PromptExecutionSettings?>(), Arg.Any<Kernel?>(), Arg.Any<CancellationToken>());
        speech.ReceivedCalls().Count().ShouldBe(2);
        var scenes = (await Stored(controlPlane)).Scenes;
        scenes[0].ShouldBe(project.Scenes[0]);
        File.Exists(Path.Combine(_media, "scene-s2.mp3")).ShouldBeTrue();
        scenes.ShouldAllBe(s => s.Status == "ready");
    }

    [Fact]
    public async Task A_Ready_Text_Card_Scene_Without_Audio_Is_Narrated_When_Speech_Becomes_Available()
    {
        var (piper, voices) = Given.BundledVoices(Directory.CreateDirectory(Path.Combine(_media, "bundle")).FullName);
        const string oldNote = "No narration: the connected AI Source has no speech service in Scout; attach your own audio in Scout Desktop, Production.";
        var project = Given.Producing(
            Given.Scene("s1", 1, "First.", "ready") with { Error = oldNote },
            Given.Scene("s2", 2, "Second.", "ready") with { Error = oldNote },
            Given.Scene("s3", 3, "Third, locked.", "ready", locked: true) with { Error = oldNote });
        project = project with { NarrationVoice = "piper:en_GB-cori-medium" };
        var controlPlane = Holding(project);

        await Given.Pipeline(controlPlane, Given.YouTube(), ScriptedChat.Answering(), Notifier(), Given.Media(), piper: Given.Piper(new FakeProcessRunner(), piper, voices))
            .RunAsync(new QueuedProject(project, Origin), CancellationToken.None);

        var scenes = (await Stored(controlPlane)).Scenes;
        scenes.Take(2).Select(s => (s.Status, s.AudioSource, s.AudioFile, s.Error)).ShouldBe([
            ("ready", "generated", "scene-s1.wav", null),
            ("ready", "generated", "scene-s2.wav", null),
        ]);
        scenes[2].ShouldBe(project.Scenes[2]);
        File.Exists(Path.Combine(_media, "scene-s1.wav")).ShouldBeTrue();
        File.Exists(Path.Combine(_media, "scene-s2.wav")).ShouldBeTrue();
        File.Exists(Path.Combine(_media, "scene-s3.wav")).ShouldBeFalse();
    }

    [Fact]
    public async Task A_Ready_Scene_With_Its_Generated_Files_Is_Still_Skipped()
    {
        var (piper, voices) = Given.BundledVoices(Directory.CreateDirectory(Path.Combine(_media, "bundle")).FullName);
        File.WriteAllBytes(Path.Combine(_media, "scene-s1.mp3"), [1]);
        File.WriteAllBytes(Path.Combine(_media, "scene-s1.png"), [1]);
        var project = Given.Producing(
            Given.Scene("s1", 1, "Whole.", "ready", audioSource: "generated", visualSource: "generated", audioFile: "scene-s1.mp3", visualFile: "scene-s1.png", durationMs: 900));
        var controlPlane = Holding(project);
        var speech = Given.Speech();
        var images = Given.Images();
        var runner = new FakeProcessRunner();

        await Given.Pipeline(controlPlane, Given.YouTube(), ScriptedChat.Answering(), Notifier(), Given.Media(speech, images), piper: Given.Piper(runner, piper, voices))
            .RunAsync(new QueuedProject(project, Origin), CancellationToken.None);

        (await Stored(controlPlane)).Scenes.ShouldBe(project.Scenes);
        speech.ReceivedCalls().ShouldBeEmpty();
        images.ReceivedCalls().ShouldBeEmpty();
        runner.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_ready_stock_scene_with_its_picture_on_disk_is_skipped_without_asking_openverse_again()
    {
        File.WriteAllBytes(Path.Combine(_media, "scene-s1.mp3"), [1]);
        File.WriteAllBytes(Path.Combine(_media, "scene-s1.jpg"), [1]);
        var project = Given.Producing(
            Given.Scene("s1", 1, "Whole.", "ready", audioSource: "generated", visualSource: "stock", audioFile: "scene-s1.mp3", visualFile: "scene-s1.jpg", durationMs: 900) with
            {
                VisualCredit = "\"Loaf\" by Ana is licensed under CC BY 2.0.",
            });
        var controlPlane = Holding(project);
        using var openverse = new OpenverseHttp();

        await Given.Pipeline(controlPlane, Given.YouTube(), ScriptedChat.Answering(), Notifier(), Given.Media(Given.Speech()), openverse: openverse)
            .RunAsync(new QueuedProject(project, Origin), CancellationToken.None);

        (await Stored(controlPlane)).Scenes.ShouldBe(project.Scenes);
        openverse.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_Ready_Text_Card_Scene_Stays_Skipped_While_No_Speech_And_No_Images_Are_Available()
    {
        const string oldNote = "No narration: the connected AI Source has no speech service in Scout; attach your own audio in Scout Desktop, Production.";
        var project = Given.Producing(Given.Scene("s1", 1, "First.", "ready") with { Error = oldNote });
        var controlPlane = Holding(project);

        await Run(controlPlane, project, Given.Media());

        (await Stored(controlPlane)).Scenes.ShouldBe(project.Scenes);
        (await Stored(controlPlane)).Status.ShouldBe("media_ready");
    }

    [Fact]
    public async Task A_Locked_Scene_And_The_Owners_Own_Media_Go_Back_Exactly_As_Read_And_Are_Never_Regenerated()
    {
        File.WriteAllBytes(Path.Combine(_media, "my-voice.wav"), [1]);
        var locked = Given.Scene("s1", 1, "Locked by the owner.", locked: true);
        var ownAudio = Given.Scene("s2", 2, "Owner recorded this.", audioSource: "owner", audioFile: "my-voice.wav", durationMs: 5000);
        var project = Given.Producing(locked, ownAudio);
        var controlPlane = Holding(project);
        var speech = Given.Speech();
        var images = Given.Images();

        await Run(controlPlane, project, Given.Media(speech, images));

        speech.ReceivedCalls().ShouldBeEmpty();
        await images.Received(1).GetImageContentsAsync(Arg.Is<TextContent>(t => t.Text == "A picture for s2"), Arg.Any<PromptExecutionSettings?>(), Arg.Any<Kernel?>(), Arg.Any<CancellationToken>());
        controlPlane.Saves.ShouldAllBe(s => s.Project.Scenes[0] == locked);
        var scene = (await Stored(controlPlane)).Scenes[1];
        (scene.AudioSource, scene.AudioFile, scene.DurationMs, scene.VisualSource, scene.VisualFile, scene.Status)
            .ShouldBe(("owner", "my-voice.wav", 5000, "generated", "scene-s2.png", "ready"));
    }

    [Fact]
    public async Task A_Planned_Scene_With_The_Owners_Image_Still_Gets_Its_Narration_And_Keeps_The_Owners_Image()
    {
        File.WriteAllBytes(Path.Combine(_media, "my-map.png"), [1]);
        var project = Given.Producing(Given.Scene("s1", 1, "Rivers bend.", visualSource: "owner", visualFile: "my-map.png"));
        var controlPlane = Holding(project);
        var speech = Given.Speech();
        var images = Given.Images();

        await Run(controlPlane, project, Given.Media(speech, images));

        await speech.Received(1).GetAudioContentsAsync("Rivers bend.", Arg.Any<PromptExecutionSettings?>(), Arg.Any<Kernel?>(), Arg.Any<CancellationToken>());
        images.ReceivedCalls().ShouldBeEmpty();
        var scene = (await Stored(controlPlane)).Scenes.ShouldHaveSingleItem();
        (scene.Status, scene.AudioSource, scene.AudioFile, scene.VisualSource, scene.VisualFile)
            .ShouldBe(("ready", "generated", "scene-s1.mp3", "owner", "my-map.png"));
    }

    [Fact]
    public async Task A_Speech_Service_That_Fails_Saves_The_Scene_Failed_And_The_Project_Failed_With_The_Reason_And_Posts_It()
    {
        var speech = Substitute.For<ITextToAudioService>();
        speech.GetAudioContentsAsync(Arg.Any<string>(), Arg.Any<PromptExecutionSettings?>(), Arg.Any<Kernel?>(), Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<AudioContent>>(_ => throw new HttpRequestException("429"));
        var project = Given.Producing(Given.Scene("s1", 1), Given.Scene("s2", 2));
        var controlPlane = Holding(project);
        var notifier = Notifier();

        await Run(controlPlane, project, Given.Media(speech, Given.Images()), notifier);

        const string reason = "The AI Source could not make the narration of scene 1; regenerate it in Scout Desktop, Production.";
        var saved = await Stored(controlPlane);
        (saved.Status, saved.Error).ShouldBe(("failed", reason));
        saved.Scenes.Select(s => (s.Status, s.Error)).ShouldBe([("failed", reason), ("planned", null)]);
        Posts(notifier).Last().ShouldBe($"The video project on sourdough stopped: {reason}");
    }

    [Fact]
    public async Task A_Media_Directory_The_Control_Plane_Cannot_Give_Fails_The_Project_Before_Any_Media_Is_Made()
    {
        var project = Given.Producing(Given.Scene("s1", 1));
        var controlPlane = Holding(project, new InvalidOperationException("Scout could not reach its control plane."));
        var speech = Given.Speech();
        var notifier = Notifier();

        await Run(controlPlane, project, Given.Media(speech, Given.Images()), notifier);

        var saved = await Stored(controlPlane);
        (saved.Status, saved.Error).ShouldBe(("failed", "Scout could not reach its control plane."));
        speech.ReceivedCalls().ShouldBeEmpty();
        Posts(notifier).Last().ShouldBe("The video project on sourdough stopped: Scout could not reach its control plane.");
    }

    [Fact]
    public async Task A_Scene_Id_That_Would_Write_Outside_The_Media_Directory_Is_Refused_And_Fails_The_Project()
    {
        var project = Given.Producing(Given.Scene("../escape", 1));
        var controlPlane = Holding(project);

        await Run(controlPlane, project, Given.Media(Given.Speech(), Given.Images()));

        var saved = await Stored(controlPlane);
        (saved.Status, saved.Error).ShouldBe(("failed", "Scout refused to write a file outside the project's media folder."));
        File.Exists(Path.Combine(Path.GetDirectoryName(_media)!, "escape.mp3")).ShouldBeFalse();
        File.Exists(Path.Combine(_media, "..", "scene-..", "escape.mp3")).ShouldBeFalse();
        Directory.GetFiles(_media).ShouldBeEmpty();
    }

    [Fact]
    public async Task An_Owner_Reorder_Mid_Run_Does_Not_Fail_The_Run_And_The_Owners_Order_Stays()
    {
        var project = Given.Producing(Given.Scene("s1", 1), Given.Scene("s2", 2), Given.Scene("s3", 3));
        var controlPlane = new RecordingControlPlane
        {
            MediaDirectory = _media,
            RefuseStaleScenes = true,
            OwnerActs = (save, saved) => save == 1
                ? saved with { Scenes = [.. saved.Scenes.Select(s => s with { Order = s.Id == "s3" ? 1 : s.Order + 1 })] }
                : null,
        };
        controlPlane.Store(project);
        var notifier = Notifier();

        await Run(controlPlane, project, Given.Media(Given.Speech(), Given.Images()), notifier);

        var saved = await Stored(controlPlane);
        saved.Status.ShouldBe("media_ready");
        saved.Scenes.Select(s => (s.Id, s.Order, s.Status)).ShouldBe([("s1", 2, "ready"), ("s2", 3, "ready"), ("s3", 1, "ready")]);
        Posts(notifier).Last().ShouldBe("Media ready for sourdough: review the scenes in Scout Desktop, Production.");
    }

    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 2)]
    public async Task A_Regenerate_Mid_Run_Stops_The_Run_Without_Saving_Over_The_Owners_Status(bool duringScene2, int narrations)
    {
        var project = Given.Producing(Given.Scene("s1", 1), Given.Scene("s2", 2), Given.Scene("s3", 3));
        static OperatorProjectState Regenerated(OperatorProjectState p) => p with { Status = "production_queued" };
        var controlPlane = new RecordingControlPlane
        {
            MediaDirectory = _media,
            OwnerActs = (save, saved) => !duringScene2 && save == 1 ? Regenerated(saved) : null,
        };
        controlPlane.Store(project);
        var speech = Substitute.For<ITextToAudioService>();
        speech.GetAudioContentsAsync(Arg.Any<string>(), Arg.Any<PromptExecutionSettings?>(), Arg.Any<Kernel?>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                if (duringScene2 && controlPlane.Saves.Count == 1)
                {
                    controlPlane.Store(Regenerated(controlPlane.GetProjectAsync("p1", CancellationToken.None).Result));
                }

                return (IReadOnlyList<AudioContent>)[new AudioContent(new byte[] { 1 }, "audio/mpeg")];
            });
        var notifier = Notifier();

        await Run(controlPlane, project, Given.Media(speech, Given.Images()), notifier);

        var saved = await Stored(controlPlane);
        saved.Status.ShouldBe("production_queued");
        controlPlane.Saves.Count.ShouldBe(1);
        saved.Scenes.Select(s => s.Status).ShouldBe(["ready", "planned", "planned"]);
        speech.ReceivedCalls().Count().ShouldBe(narrations);
        Posts(notifier).ShouldBe(["Producing narration and visuals for sourdough..."]);
    }

    [Fact]
    public async Task A_Save_The_Control_Plane_Refuses_Because_The_Project_Is_No_Longer_Producing_Stops_The_Run_Without_A_Failed_Save()
    {
        var project = Given.Producing(Given.Scene("s1", 1), Given.Scene("s2", 2), Given.Scene("s3", 3));
        var controlPlane = new RecordingControlPlane { MediaDirectory = _media, NoLongerProducingOn = save => save == 2 };
        controlPlane.Store(project);
        var speech = Given.Speech();
        var notifier = Notifier();

        await Run(controlPlane, project, Given.Media(speech, Given.Images()), notifier);

        controlPlane.Saves.Select(s => s.Project.Status).ShouldBe(["producing", "producing"]);
        (await Stored(controlPlane)).Scenes.Select(s => s.Status).ShouldBe(["ready", "planned", "planned"]);
        speech.ReceivedCalls().Count().ShouldBe(2);
        Posts(notifier).ShouldBe(["Producing narration and visuals for sourdough..."]);
    }

    [Fact]
    public async Task A_Producing_Project_With_Neither_Scenes_Nor_A_Script_Fails_Saying_So()
    {
        var project = Given.Project(status: "producing");
        var controlPlane = Holding(project);

        await Run(controlPlane, project, Given.Media());

        var saved = await Stored(controlPlane);
        (saved.Status, saved.Error).ShouldBe(("failed", "The project has no script to produce; write the script first."));
    }

    private static OperatorProjectState OwnVideoProducing() => Given.Project(status: "producing") with
    {
        Origin = "desktop",
        Format = "own_video",
        RecordingFile = "owner-recording-1a.mp4",
        RecordingMs = 50_000,
        Script = new OperatorScript(
            string.Empty,
            [
                new OperatorScriptSection(string.Empty, "Today we bake bread.") { StartMs = 0, EndMs = 21000 },
                new OperatorScriptSection(string.Empty, "First the starter.") { StartMs = 22000, EndMs = 44000 },
            ],
            string.Empty, [], string.Empty, [], []),
    };

    [Fact]
    public async Task Producing_An_Own_Video_Project_Makes_No_Narration_And_No_Picture_And_Ends_Media_Ready()
    {
        var project = OwnVideoProducing();
        var controlPlane = Holding(project);
        var speech = Given.Speech();
        var images = Given.Images();
        using var openverse = new OpenverseHttp();
        var chat = ScriptedChat.Answering("{\"title_variants\":[\"Bread\"],\"description\":\"How I bake.\",\"tags\":[],\"chapters\":[{\"scene\":1,\"title\":\"Starting\"}]}");
        var pipeline = Given.Pipeline(
            controlPlane, Given.YouTube(), chat, Notifier(), Given.Media(speech, images), openverse: openverse);

        await pipeline.RunAsync(new QueuedProject(project, null), CancellationToken.None);
        var produced = await Stored(controlPlane);

        produced.Status.ShouldBe("media_ready");
        produced.Scenes.Select(s => (s.Id, s.Status, s.AudioSource, s.VisualSource, s.AudioFile, s.VisualFile, s.ClipStartMs, s.ClipEndMs)).ShouldBe([
            ("s1", "ready", "recording", "recording", (string?)null, (string?)null, 0, 22000),
            ("s2", "ready", "recording", "recording", (string?)null, (string?)null, 22000, 50_000),
        ]);
        produced.Script.ShouldNotBeNull().Sections.Select(s => (s.Heading, s.Narration)).ShouldBe([("Starting", "Today we bake bread. First the starter.")]);
        controlPlane.Saves.Select(s => s.Project.Status).ShouldBe(["producing", "media_ready"]);
        speech.ReceivedCalls().ShouldBeEmpty();
        images.ReceivedCalls().ShouldBeEmpty();
        openverse.Requests.ShouldBeEmpty();
        chat.Requests.Count.ShouldBe(1);
        Directory.GetFiles(_media).ShouldBeEmpty();
    }

    [Fact]
    public async Task Producing_An_Own_Video_Project_Again_With_A_New_Direction_Names_New_Chapters_And_Keeps_The_Clips()
    {
        var project = OwnVideoProducing();
        var controlPlane = Holding(project);
        var chat = ScriptedChat.Answering(
            "{\"title_variants\":[\"Bread\"],\"description\":\"How I bake.\",\"tags\":[\"bread\"],\"chapters\":[{\"scene\":1,\"title\":\"Starting\"}]}",
            "{\"title_variants\":[\"Sourdough for kids\"],\"description\":\"Baking with children.\",\"tags\":[\"kids\"],\"chapters\":[{\"scene\":1,\"title\":\"Mixing\"},{\"scene\":2,\"title\":\"The starter\"}]}");
        var pipeline = Given.Pipeline(controlPlane, Given.YouTube(), chat, Notifier(), Given.Media(Given.Speech(), Given.Images()));
        await pipeline.RunAsync(new QueuedProject(project, null), CancellationToken.None);
        var first = await Stored(controlPlane);

        var again = first with { Status = "producing", Direction = "For children, playful." };
        controlPlane.Store(again);
        await pipeline.RunAsync(new QueuedProject(again, null), CancellationToken.None);
        var second = await Stored(controlPlane);

        second.Status.ShouldBe("media_ready");
        second.Scenes.ShouldBe(first.Scenes, "producing again leaves the clips as they are");
        var script = second.Script.ShouldNotBeNull();
        script.Sections.Select(s => (s.Heading, s.Narration, s.StartMs, s.EndMs)).ShouldBe([
            ("Mixing", "Today we bake bread.", 0, 22000),
            ("The starter", "First the starter.", 22000, 50_000),
        ]);
        script.TitleVariants.ShouldBe(["Sourdough for kids"]);
        script.Description.ShouldBe("Baking with children.");
        script.Tags.ShouldBe(["kids"]);
        chat.Requests.Count.ShouldBe(2);
        chat.Requests[1].Single(m => m.Role == AuthorRole.System).Text.ShouldContain("Direction the owner gave for this video: For children, playful.");
    }

    [Fact]
    public async Task An_Own_Video_Production_Whose_Chapter_Request_Cannot_Reach_The_Model_Fails_With_The_AI_Source_Sentence_And_Saves_No_Scenes()
    {
        var project = OwnVideoProducing();
        var controlPlane = Holding(project);
        var chat = new ScriptedChat(() => throw new HttpRequestException("connection refused"));

        await Run(controlPlane, project, Given.Media(Given.Speech(), Given.Images()), chat: chat);

        var saved = await Stored(controlPlane);
        (saved.Status, saved.Error).ShouldBe(("failed", "The AI model did not answer; check the AI Source in Scout Desktop."));
        saved.Scenes.ShouldBeEmpty();
        controlPlane.Saves.Select(s => s.Project.Scenes.Count).ShouldBe([0]);
    }

    [Fact]
    public async Task An_Own_Video_Production_Whose_Chapters_Come_Back_Wrong_Twice_Fails_With_The_Chapters_Sentence_And_Saves_No_Scenes()
    {
        var project = OwnVideoProducing();
        var controlPlane = Holding(project);
        var chat = ScriptedChat.Answering("{\"chapters\":[{\"scene\":2,\"title\":\"Late\"}]}", "not json");

        await Run(controlPlane, project, Given.Media(Given.Speech(), Given.Images()), chat: chat);

        var saved = await Stored(controlPlane);
        (saved.Status, saved.Error).ShouldBe(("failed", "The AI model did not name the chapters of the recording in the expected form, twice."));
        saved.Scenes.ShouldBeEmpty();
        chat.Requests.Count.ShouldBe(2);
    }

    private static OperatorProjectState AnimatedProducing() => Given.Producing() with
    {
        Format = "animated_character",
        Direction = "Warm kitchen light",
        Presenter = "A cheerful baker in a flour-dusted apron",
    };

    [Fact]
    public async Task An_Animated_Character_Scene_Gets_Its_Own_Picture_Like_Generated_Pictures()
    {
        var project = AnimatedProducing();
        var controlPlane = Holding(project);
        var images = Given.Images();

        await Run(controlPlane, project, Given.Media(Given.Speech(), images));

        var saved = await Stored(controlPlane);
        (saved.Status, saved.Error, saved.PresenterFile).ShouldBe(("media_ready", (string?)null, (string?)null));
        saved.Scenes.Select(s => (s.Status, s.AudioSource, s.AudioFile, s.VisualSource, s.VisualFile)).ShouldBe([
            ("ready", "generated", "scene-s1.mp3", "generated", "scene-s1.png"),
            ("ready", "generated", "scene-s2.mp3", "generated", "scene-s2.png"),
            ("ready", "generated", "scene-s3.mp3", "generated", "scene-s3.png"),
        ]);
        images.ReceivedCalls().Select(c => c.GetArguments()[0].ShouldBeOfType<TextContent>().Text).ShouldBe(["Picture 1", "Picture 2", "Picture 3"]);
        Directory.GetFiles(_media, "presenter-*").ShouldBeEmpty();
    }

    [Fact]
    public async Task A_Scene_Of_The_First_Animated_Character_Shown_By_The_Presenter_Alone_Gets_Its_Picture_On_The_Next_Production()
    {
        File.WriteAllBytes(Path.Combine(_media, "scene-s1.mp3"), [1]);
        var project = AnimatedProducing() with
        {
            Scenes = [Given.Scene("s1", 1, status: "ready", audioSource: "generated", visualSource: "presenter", audioFile: "scene-s1.mp3", durationMs: 1600)],
        };
        var controlPlane = Holding(project);
        var images = Given.Images();

        await Run(controlPlane, project, Given.Media(Given.Speech(), images));

        var scene = (await Stored(controlPlane)).Scenes.ShouldHaveSingleItem();
        (scene.AudioFile, scene.VisualSource, scene.VisualFile).ShouldBe(("scene-s1.mp3", "generated", "scene-s1.png"));
        images.ReceivedCalls().ShouldHaveSingleItem();
    }
}
