#pragma warning disable SKEXP0001
using Microsoft.SemanticKernel;
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
    public async Task A_Project_Is_Planned_In_The_Owners_Visual_Brief_And_Its_Brief_And_Motion_Go_Back_As_Read()
    {
        var project = Given.Producing() with { VisualBrief = "Bold comic panels, it's: loud.", Motion = "still" };
        var controlPlane = Holding(project);
        var chat = ScriptedChat.Answering(Given.Prompts(3));

        await Run(controlPlane, project, Given.Media(Given.Speech(), Given.Images()), chat: chat);

        chat.Requests.ShouldHaveSingleItem().Single(m => m.Role == Microsoft.SemanticKernel.ChatCompletion.AuthorRole.System).Text
            .ShouldContain("Visual style the owner asked for: Bold comic panels, it's: loud.", Case.Sensitive);
        controlPlane.Saves.ShouldNotBeEmpty();
        controlPlane.Saves.ShouldAllBe(s => s.Project.VisualBrief == "Bold comic panels, it's: loud." && s.Project.Motion == "still");
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
            "No picture: the connected AI Source has no image service in Scout; the scene shows a text card, or attach your own image in Scout Desktop, Production.");
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
}
