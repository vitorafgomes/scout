#pragma warning disable SKEXP0001
using Microsoft.Extensions.Time.Testing;
using Scout.Conversation.Abstractions;
using Scout.Operator.Tests.Fakes;

namespace Scout.Operator.Tests;

public sealed class AssemblyTests : IDisposable
{
    private const string Origin = "discord:3333:42";

    private readonly string _media = Directory.CreateTempSubdirectory("scout-assembly-").FullName;

    public void Dispose() => Directory.Delete(_media, recursive: true);

    private OperatorProjectState Assembling(OperatorPackage? package = null)
    {
        foreach (var file in new[] { "scene-s1.mp3", "scene-s1.png", "scene-s2.mp3", "my-voice.wav" })
        {
            File.WriteAllBytes(Path.Combine(_media, file), [1, 2, 3]);
        }

        return Given.Producing(
            Given.Scene("s1", 1, "Bread in ten minutes? Here is how.", "ready", audioSource: "generated", visualSource: "generated",
                audioFile: "scene-s1.mp3", visualFile: "scene-s1.png", durationMs: 2000),
            Given.Scene("s2", 2, "Feed it daily.", "ready", audioSource: "generated", audioFile: "scene-s2.mp3", durationMs: 1000),
            Given.Scene("s3", 3, "Subscribe.", "ready", audioSource: "owner", audioFile: "my-voice.wav", durationMs: 900)) with
        {
            Status = "assembling",
            Package = package,
        };
    }

    private RecordingControlPlane Holding(OperatorProjectState project, Func<string, bool>? failOn = null, Func<int, bool>? noLongerAssemblingOn = null)
    {
        var controlPlane = new RecordingControlPlane
        {
            MediaDirectory = _media,
            Mandate = Given.Mandate(pillars: ["bread"]),
            FailOn = failOn ?? (_ => false),
            NoLongerAssemblingOn = noLongerAssemblingOn ?? (_ => false),
        };
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

    private static Task Run(RecordingControlPlane controlPlane, OperatorProjectState project, FakeProcessRunner runner, IOriginNotifier? notifier = null)
        => Given.Pipeline(controlPlane, Given.YouTube(), ScriptedChat.Answering("{}"), notifier ?? Notifier(), Given.Media(), runner: runner)
            .RunAsync(new QueuedProject(project, Origin), CancellationToken.None);

    private List<string> Files() => [.. Directory.GetFiles(_media).Select(f => Path.GetFileName(f)!).Order(StringComparer.Ordinal)];

    [Fact]
    public async Task An_Assembling_Project_Is_Saved_Assembled_With_Its_Video_Shorts_Thumbnail_Checks_And_First_Metadata_And_The_Chat_Hears_It_Is_Ready()
    {
        var project = Assembling();
        var controlPlane = Holding(project);
        var runner = new FakeProcessRunner { ProbedMs = 12_000 };
        var notifier = Notifier();

        await Run(controlPlane, project, runner, notifier);

        var save = controlPlane.Saves.ShouldHaveSingleItem().Project;
        save.Status.ShouldBe("assembled");
        save.Scenes.ShouldBe(project.Scenes);
        var package = save.Package.ShouldNotBeNull();
        (package.Status, package.VideoFile, package.ThumbnailFile, package.DurationMs, package.Error).ShouldBe(
            ("assembled", "video.mp4", "thumbnail.png", 36_000, null));
        package.Shorts.ShouldBe([new OperatorShort("short-1.mp4", 0, 36_000, "Bread in ten minutes?")]);
        package.Checks.Select(c => (c.Name, c.Result)).ShouldBe([
            ("duration", "pass"), ("loudness", "pass"), ("captions", "pass"), ("placeholders", "pass"), ("metadata", "pass"), ("discoverability", "warn"),
        ]);
        (package.Title, package.Description, package.RightsConfirmed, package.ApprovedAt, package.MetadataEdited).ShouldBe(
            ("A", "How to bake.", false, null, false));
        package.Tags.ShouldBe(["bread"]);
        runner.Measured.ShouldBe([
            Path.Combine(_media, "scene-s1.mp3"), Path.Combine(_media, "scene-s2.mp3"), Path.Combine(_media, "my-voice.wav"),
        ]);
        runner.Renders.Count(r => r.Contains("-t") && r[r.ToList().IndexOf("-t") + 1] == "12.000").ShouldBe(3);
        Files().Where(f => !f.StartsWith("segment-", StringComparison.Ordinal))
            .ShouldBe(["my-voice.wav", "scene-s1.mp3", "scene-s1.png", "scene-s2.mp3", "short-1.mp4", "thumbnail.png", "video.mp4"]);
        Files().Count(f => f.StartsWith("segment-", StringComparison.Ordinal)).ShouldBe(3);
        Posts(notifier).ShouldBe([
            "Assembling sourdough...",
            "Video ready to review for sourdough: open Scout Desktop, YouTube operator.",
        ]);
    }

    [Fact]
    public async Task An_Animated_Character_Project_Is_Assembled_With_Its_Presenter_Picture_And_Says_Where_The_Mouth_Is_Missing()
    {
        File.WriteAllBytes(Path.Combine(_media, "presenter-0a1b2c3d4e5f.png"), [1, 2, 3]);
        File.WriteAllBytes(Path.Combine(_media, "scene-s1.mp3"), [1, 2, 3]);
        var project = Given.Producing(
            Given.Scene("s1", 1, "Bread in ten minutes? Here is how.", "ready", audioSource: "generated", visualSource: "presenter",
                audioFile: "scene-s1.mp3", durationMs: 2000)) with
        {
            Status = "assembling",
            Format = "animated_character",
            PresenterFile = "presenter-0a1b2c3d4e5f.png",
        };
        var controlPlane = Holding(project);
        var runner = new FakeProcessRunner { ProbedMs = 31_000, ReportsPicture = true };

        await Run(controlPlane, project, runner);

        var save = controlPlane.Saves.ShouldHaveSingleItem().Project;
        save.Status.ShouldBe("assembled");
        runner.Renders[0].ShouldContain(Path.Combine(_media, "presenter-0a1b2c3d4e5f.png"));
        save.Package.ShouldNotBeNull().Checks[^1].ShouldBe(new OperatorCheck(
            "presenter", "warn", "Scenes 1: Mark the mouth on the presenter picture in Scout Desktop, Production, so the character speaks."));
    }

    [Fact]
    public async Task The_Projects_Background_Is_Rendered_Under_The_Scenes_Without_A_Picture_And_Goes_Back_As_Read()
    {
        File.WriteAllBytes(Path.Combine(_media, "studio.png"), [5, 6]);
        var project = Assembling() with { BackgroundFile = "studio.png" };
        var controlPlane = Holding(project);
        var runner = new FakeProcessRunner { ProbedMs = 12_000 };

        await Run(controlPlane, project, runner);

        var save = controlPlane.Saves.ShouldHaveSingleItem().Project;
        save.BackgroundFile.ShouldBe("studio.png");
        save.Scenes.ShouldBe(project.Scenes);
        runner.Renders.Count(r => r.Contains(Path.Combine(_media, "studio.png"))).ShouldBe(2);
        runner.Renders.ShouldNotContain(r => r.Contains("color=c=0x14181f:s=1920x1080:r=30"));
    }

    [Theory]
    [InlineData("still", "scale=1920:1080:", "zoompan")]
    [InlineData("gentle", "zoompan=z=1+0.08*", "scale=1920:1080:")]
    [InlineData("dynamic", "zoompan=z=1+0.16*", "zoompan=z=1+0.08*")]
    public async Task The_Picture_Scene_Moves_Under_The_Projects_Motion_And_The_Motion_Goes_Back_As_Read(string motion, string present, string absent)
    {
        var project = Assembling() with { Motion = motion };
        var controlPlane = Holding(project);
        var runner = new FakeProcessRunner { ProbedMs = 12_000 };

        await Run(controlPlane, project, runner);

        var picture = runner.Renders.Single(r => r.Contains(Path.Combine(_media, "scene-s1.png")) && r.Contains("-filter_complex"));
        var graph = picture.SkipWhile(a => a != "-filter_complex").ElementAt(1);
        graph.ShouldContain(present, Case.Sensitive);
        graph.ShouldNotContain(absent, Case.Sensitive);
        controlPlane.Saves.ShouldHaveSingleItem().Project.Motion.ShouldBe(motion);
    }

    [Fact]
    public async Task A_120_Character_Title_Variant_Or_A_200_Character_Topic_Still_Saves_The_Package_Assembled_Within_YouTubes_Limits()
    {
        var long120 = string.Join(' ', Enumerable.Repeat("Sourdough", 12)) + " bread";
        var topic200 = string.Concat(Enumerable.Repeat("sourdough ", 19)) + "starter bread";
        foreach (var (titles, topic) in new[] { ((IReadOnlyList<string>)[long120], "sourdough"), ([], topic200) })
        {
            var stored = Assembling();
            var project = stored with { Topic = topic, Script = stored.Script! with { TitleVariants = titles } };
            var controlPlane = Holding(project);

            await Run(controlPlane, project, new FakeProcessRunner { ProbedMs = 12_000 });

            var save = controlPlane.Saves.ShouldHaveSingleItem().Project;
            save.Status.ShouldBe("assembled");
            var package = save.Package.ShouldNotBeNull();
            package.Status.ShouldBe("assembled");
            package.Title.Length.ShouldBeInRange(1, 100);
            package.Title.ShouldNotEndWith(" ");
            package.Checks.Single(c => c.Name == "metadata").Result.ShouldBe("pass");
        }
    }

    [Fact]
    public async Task The_Package_Says_When_It_Was_Assembled_By_The_Workers_Clock_And_What_Its_Thumbnail_Shows()
    {
        var project = Assembling();
        var controlPlane = Holding(project);
        var at = new DateTimeOffset(2026, 11, 14, 14, 12, 0, TimeSpan.Zero);

        await Given.Pipeline(
                controlPlane, Given.YouTube(), ScriptedChat.Answering("{}"), Notifier(), Given.Media(), new FakeTimeProvider(at), new FakeProcessRunner { ProbedMs = 12_000 })
            .RunAsync(new QueuedProject(project, Origin), CancellationToken.None);

        var save = controlPlane.Saves.ShouldHaveSingleItem().Project;
        save.Status.ShouldBe("assembled");
        var package = save.Package.ShouldNotBeNull();
        (package.VideoFile, package.ThumbnailFile).ShouldBe(("video.mp4", "thumbnail.png"));
        package.AssembledAt.ShouldBe("2026-11-14T14:12:00.0000000+00:00", "the package carries the time of its assembly, read from the worker's clock");
        package.ThumbnailDescription.ShouldBe(
            "A frame from the video at a tenth of its length.", "the package carries what its thumbnail shows, here the frame taken without an image service");
        (package.MetadataSavedAt, package.RightsConfirmedAt).ShouldBe((null, null), "the worker never sets the owner's times");
    }

    [Fact]
    public async Task Metadata_The_Owner_Edited_And_The_Owners_Rights_And_Approval_Go_Back_Exactly_As_Read()
    {
        var owners = new OperatorPackage("assembled", "video.mp4", [], null, [], true, "2026-11-03T13:00:00Z",
            "My own title", "My own description", ["mine"], 1000, null, true)
        {
            MetadataSavedAt = "2026-11-03T12:20:00Z",
            RightsConfirmedAt = "2026-11-03T12:31:00Z",
        };
        var project = Assembling(owners);
        var controlPlane = Holding(project);

        await Run(controlPlane, project, new FakeProcessRunner { ProbedMs = 12_000 });

        var package = controlPlane.Saves.ShouldHaveSingleItem().Project.Package.ShouldNotBeNull();
        (package.Title, package.Description, package.RightsConfirmed, package.ApprovedAt, package.MetadataEdited).ShouldBe(
            ("My own title", "My own description", true, "2026-11-03T13:00:00Z", true));
        (package.MetadataSavedAt, package.RightsConfirmedAt).ShouldBe(
            ("2026-11-03T12:20:00Z", "2026-11-03T12:31:00Z"), "when the owner saved the metadata and confirmed the rights goes back exactly as read");
        package.Tags.ShouldBe(["mine"]);
        package.Checks.Single(c => c.Name == "discoverability").Detail.ShouldContain("no chapters list");
    }

    [Fact]
    public async Task Without_A_Usable_Ffmpeg_The_Project_Fails_With_The_Readiness_Note_And_Nothing_Is_Rendered()
    {
        var project = Assembling();
        var controlPlane = Holding(project);
        var runner = new FakeProcessRunner { Answer = (program, _) => throw new InvalidOperationException(ProcessRunner.NotStarted(program)) };
        var notifier = Notifier();

        await Run(controlPlane, project, runner, notifier);

        var save = controlPlane.Saves.ShouldHaveSingleItem().Project;
        (save.Status, save.Error).ShouldBe(("failed", "Scout could not run ffmpeg, so videos cannot be assembled."));
        (save.Package!.Status, save.Package.Error).ShouldBe(("failed", "Scout could not run ffmpeg, so videos cannot be assembled."));
        runner.Renders.ShouldBeEmpty();
        Posts(notifier)[^1].ShouldBe("The video project on sourdough stopped: Scout could not run ffmpeg, so videos cannot be assembled.");
    }

    [Fact]
    public async Task A_Narration_Ffmpeg_Cannot_Read_Fails_The_Project_With_A_Sentence_Naming_The_File()
    {
        var project = Assembling();
        var controlPlane = Holding(project);
        var runner = new FakeProcessRunner
        {
            Answer = (_, arguments) => arguments.Count >= 2 && arguments[^2] == "-i"
                ? new ProcessResult(1, string.Empty, "Invalid data found when processing input")
                : null,
        };

        await Run(controlPlane, project, runner);

        var save = controlPlane.Saves.ShouldHaveSingleItem().Project;
        (save.Status, save.Error).ShouldBe(("failed", "Scout could not read the length of scene-s1.mp3."));
        runner.Renders.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_Join_That_Fails_Saves_The_Project_Failed_Removes_The_Partial_Files_And_The_Next_Assembly_Renders_Only_The_Join_Again()
    {
        var project = Assembling(new OperatorPackage("assembled", "video.mp4", [new OperatorShort("short-1.mp4", 0, 30_000, "Old")],
            "thumbnail.png", [new OperatorCheck("duration", "pass", "old")], false, null, "A", "How to bake.", ["bread"], 30_000, null, false)
        {
            AssembledAt = "2026-11-03T11:00:00.0000000+00:00",
            ThumbnailDescription = "A frame from the video at a tenth of its length.",
        });
        File.WriteAllBytes(Path.Combine(_media, "video.mp4"), [9]);
        File.WriteAllBytes(Path.Combine(_media, "short-1.mp4"), [9]);
        var controlPlane = Holding(project);
        var failing = new FakeProcessRunner
        {
            ProbedMs = 12_000,
            Answer = (_, arguments) => arguments.Contains("concat") ? new ProcessResult(1, string.Empty, "concat.txt: Invalid data found when processing input\n") : null,
        };
        var notifier = Notifier();

        await Run(controlPlane, project, failing, notifier);

        var failed = controlPlane.Saves.ShouldHaveSingleItem().Project;
        const string reason = "Scout could not join the scenes into the video (ffmpeg: concat.txt: Invalid data found when processing input)";
        (failed.Status, failed.Error).ShouldBe(("failed", reason));
        var package = failed.Package.ShouldNotBeNull();
        (package.Status, package.Error, package.VideoFile, package.ThumbnailFile, package.DurationMs).ShouldBe(("failed", reason, null, null, 0));
        (package.Shorts.Count, package.Checks.Count, package.Title).ShouldBe((0, 0, "A"));
        (package.AssembledAt, package.ThumbnailDescription).ShouldBe((null, null), "a failed package has no assembly time and no thumbnail to describe");
        Files().Where(f => !f.StartsWith("segment-", StringComparison.Ordinal))
            .ShouldBe(["my-voice.wav", "scene-s1.mp3", "scene-s1.png", "scene-s2.mp3"]);
        Files().Count(f => f.StartsWith("segment-", StringComparison.Ordinal)).ShouldBe(3);
        Posts(notifier)[^1].ShouldBe($"The video project on sourdough stopped: {reason}");

        controlPlane.Store(project);
        var again = new FakeProcessRunner { ProbedMs = 12_000 };
        await Run(controlPlane, project, again);

        controlPlane.Saves[^1].Project.Status.ShouldBe("assembled");
        again.Renders.Count(r => r.Any(a => a.EndsWith("[1:a]aresample=48000,apad[a]", StringComparison.Ordinal))).ShouldBe(0);
        again.Renders.Count(r => r.Contains("concat")).ShouldBe(1);
        Files().Count(f => f.StartsWith("segment-", StringComparison.Ordinal)).ShouldBe(3);
    }

    [Fact]
    public async Task After_A_Saved_Assembly_A_Second_One_With_One_Scene_Changed_Renders_Only_That_Scene_And_Removes_The_Stale_Segment_And_Shorts()
    {
        var project = Assembling();
        var controlPlane = Holding(project);
        await Run(controlPlane, project, new FakeProcessRunner { ProbedMs = 12_000 });
        var firstSegments = Files().Where(f => f.StartsWith("segment-", StringComparison.Ordinal)).ToList();
        File.WriteAllBytes(Path.Combine(_media, "short-2.mp4"), [9]);
        File.WriteAllBytes(Path.Combine(_media, "short-3.mp4"), [9]);
        File.WriteAllBytes(Path.Combine(_media, "scene-s2.mp3"), [7, 7, 7, 7]);
        File.SetLastWriteTimeUtc(Path.Combine(_media, "scene-s2.mp3"), DateTime.UtcNow.AddMinutes(1));
        controlPlane.Store(project);
        var again = new FakeProcessRunner { ProbedMs = 12_000 };

        await Run(controlPlane, project, again);

        controlPlane.Saves[^1].Project.Status.ShouldBe("assembled");
        var segmentRenders = again.Renders.Where(r => r.Any(a => a.Contains("aresample=48000,apad", StringComparison.Ordinal))).ToList();
        segmentRenders.ShouldHaveSingleItem().ShouldContain(Path.Combine(_media, "scene-s2.mp3"));
        var segments = Files().Where(f => f.StartsWith("segment-", StringComparison.Ordinal)).ToList();
        segments.Count.ShouldBe(3);
        segments.Except(firstSegments).Count().ShouldBe(1);
        Files().ShouldNotContain("short-2.mp4");
        Files().ShouldNotContain("short-3.mp4");
        Files().ShouldContain("short-1.mp4");
    }

    [Fact]
    public async Task A_Media_Folder_That_Cannot_Be_Listed_After_The_Save_Still_Leaves_The_Project_Assembled_And_The_Chat_Hears_It_Is_Ready()
    {
        var project = Assembling();
        var moved = _media + "-moved";
        var controlPlane = new RecordingControlPlane
        {
            MediaDirectory = _media,
            Mandate = Given.Mandate(pillars: ["bread"]),
            OwnerActs = (_, saved) =>
            {
                Directory.Move(_media, moved);
                return null;
            },
        };
        controlPlane.Store(project);
        var notifier = Notifier();

        try
        {
            await Run(controlPlane, project, new FakeProcessRunner { ProbedMs = 12_000 }, notifier);
        }
        finally
        {
            Directory.Move(moved, _media);
        }

        controlPlane.Saves.ShouldHaveSingleItem().Project.Status.ShouldBe("assembled");
        (await controlPlane.GetProjectAsync(project.Id, CancellationToken.None)).Status.ShouldBe("assembled");
        Posts(notifier)[^1].ShouldBe("Video ready to review for sourdough: open Scout Desktop, YouTube operator.");
    }

    [Fact]
    public async Task The_Shorts_Thumbnail_And_Loudness_Run_At_The_Same_Time()
    {
        var project = Assembling();
        var loudnessStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = new FakeProcessRunner
        {
            ProbedMs = 12_000,
            Answer = (_, arguments) =>
            {
                if (arguments.Contains("loudnorm=print_format=json"))
                {
                    loudnessStarted.TrySetResult();
                }

                return null;
            },
        };
        var images = Gated(loudnessStarted.Task);
        var stage = Given.Assembly(runner, new VisualGenerator(Given.Media(images: images), Given.Capabilities(Given.Media(images: images)), new FakeTimeProvider(Given.Now)));

        var render = await stage.RenderAsync(Given.Tools(), project, _media, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));

        (render.Thumbnail, render.Shorts.Count, render.Loudness).ShouldBe(("thumbnail.png", 1, -14.21));
        File.ReadAllBytes(Path.Combine(_media, "thumbnail.png")).ShouldBe(new byte[] { 5, 5 });
    }

    [Fact]
    public async Task A_Short_That_Fails_Leaves_No_Thumbnail_Because_The_Partial_Files_Are_Removed_Only_Once_The_Thumbnail_Is_Done()
    {
        var project = Assembling();
        var thumbnailMayFinish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = new FakeProcessRunner
        {
            ProbedMs = 12_000,
            Answer = (_, arguments) => arguments.Any(a => a.Contains("split=2", StringComparison.Ordinal))
                ? new ProcessResult(1, string.Empty, "Error initializing filters\n")
                : null,
        };
        var images = Gated(thumbnailMayFinish.Task);
        var stage = Given.Assembly(runner, new VisualGenerator(Given.Media(images: images), Given.Capabilities(Given.Media(images: images)), new FakeTimeProvider(Given.Now)));

        var rendering = stage.RenderAsync(Given.Tools(), project, _media, CancellationToken.None);
        await Task.Delay(100);
        var endedBeforeTheThumbnail = rendering.IsCompleted;
        thumbnailMayFinish.SetResult();
        var ex = await Should.ThrowAsync<InvalidOperationException>(() => rendering.WaitAsync(TimeSpan.FromSeconds(10)));
        await Task.Delay(100);

        endedBeforeTheThumbnail.ShouldBeFalse();
        ex.Message.ShouldBe("Scout could not cut Short 1 (ffmpeg: Error initializing filters)");
        Files().ShouldNotContain("thumbnail.png");
        Files().ShouldNotContain("video.mp4");
    }

    private static Microsoft.SemanticKernel.TextToImage.ITextToImageService Gated(Task gate)
    {
        var images = Substitute.For<Microsoft.SemanticKernel.TextToImage.ITextToImageService>();
        images.GetImageContentsAsync(default!, default, default, default)
            .ReturnsForAnyArgs(async _ =>
            {
                await gate;
                return (IReadOnlyList<Microsoft.SemanticKernel.ImageContent>)[new Microsoft.SemanticKernel.ImageContent(new byte[] { 5, 5 }, "image/png")];
            });
        return images;
    }

    [Fact]
    public async Task A_Save_The_Control_Plane_Refuses_Because_The_Project_Is_No_Longer_Assembling_Stops_The_Run_Without_A_Failed_Save_Or_Notice()
    {
        var project = Assembling();
        var controlPlane = Holding(project, noLongerAssemblingOn: _ => true);
        var notifier = Notifier();

        await Run(controlPlane, project, new FakeProcessRunner { ProbedMs = 12_000 }, notifier);

        controlPlane.Saves.ShouldHaveSingleItem().Project.Status.ShouldBe("assembled");
        Posts(notifier).ShouldBe(["Assembling sourdough..."]);
    }

    [Fact]
    public async Task A_Project_No_Longer_Assembling_When_The_Run_Reads_It_Renders_And_Saves_Nothing()
    {
        var project = Assembling();
        var controlPlane = Holding(project with { Status = "assembly_queued" });
        var runner = new FakeProcessRunner();
        var notifier = Notifier();

        await Run(controlPlane, project, runner, notifier);

        controlPlane.Saves.ShouldBeEmpty();
        runner.Renders.ShouldBeEmpty();
        Posts(notifier).ShouldBe(["Assembling sourdough..."]);
    }

    [Fact]
    public async Task A_Package_The_Control_Plane_Cannot_Take_Removes_The_Rendered_Files_And_The_Project_Is_Saved_Failed_With_Why()
    {
        var project = Assembling();
        var controlPlane = Holding(project, failOn: status => status == "assembled");

        await Run(controlPlane, project, new FakeProcessRunner { ProbedMs = 12_000 });

        controlPlane.Saves.Select(s => (s.Project.Status, s.Project.Error)).ShouldBe([
            ("assembled", null), ("failed", "Scout could not reach its control plane."),
        ]);
        Files().ShouldNotContain("video.mp4");
        Files().ShouldNotContain("thumbnail.png");
        Files().ShouldNotContain("short-1.mp4");
    }

    private static OperatorProjectState OwnVideoAssembling(string thirdSceneText)
    {
        static OperatorScene Clip(string id, int order, string narration, int startMs, int endMs)
        {
            var scene = Given.Scene(id, order, narration, "ready", audioSource: "recording", visualSource: "recording", durationMs: endMs - startMs);
            return scene with { VisualPrompt = string.Empty, ClipStartMs = startMs, ClipEndMs = endMs };
        }

        return Given.Project(status: "assembling") with
        {
            Origin = "desktop",
            Format = "own_video",
            RecordingFile = "owner-recording-1a.mp4",
            RecordingMs = 90_000,
            Script = new OperatorScript(
                string.Empty,
                [
                    new OperatorScriptSection("Getting started", "Today we bake. First the starter.") { StartMs = 0, EndMs = 50_000 },
                    new OperatorScriptSection("Into the oven", "Then the oven.") { StartMs = 50_000, EndMs = 60_000 },
                    new OperatorScriptSection("Out of the oven", "And we eat.") { StartMs = 60_000, EndMs = 90_000 },
                ],
                string.Empty,
                ["Bread at home"],
                "How I bake.",
                ["bread"],
                []),
            Scenes =
            [
                Clip("s1", 1, "Today we bake.", 0, 25_000),
                Clip("s2", 2, "First the starter.", 25_000, 50_000),
                Clip("s3", 3, thirdSceneText, 50_000, 60_000),
                Clip("s4", 4, "And we eat.", 60_000, 90_000),
            ],
        };
    }

    [Fact]
    public async Task An_Own_Video_Description_Lists_The_Chapters_At_Their_Scenes()
    {
        File.WriteAllBytes(Path.Combine(_media, "owner-recording-1a.mp4"), [1, 2, 3]);
        var project = OwnVideoAssembling("Then the oven.");
        var controlPlane = Holding(project);
        var runner = new FakeProcessRunner();

        await Run(controlPlane, project, runner);

        var package = controlPlane.Saves.ShouldHaveSingleItem().Project.Package.ShouldNotBeNull();
        (package.Status, package.DurationMs, package.Title).ShouldBe(("assembled", 90_000, "Bread at home"));
        package.Description.ShouldBe("How I bake.\n\n0:00 Getting started\n0:50 Into the oven\n1:00 Out of the oven");
        package.Checks.Single(c => c.Name == "placeholders").Result.ShouldBe("pass");
        runner.Measured.ShouldNotContain(Path.Combine(_media, "owner-recording-1a.mp4"));
    }

    [Fact]
    public async Task An_Own_Video_Keeps_Every_Chapter_After_The_Owner_Edits_The_Captions_Of_A_Chapters_First_Scene()
    {
        File.WriteAllBytes(Path.Combine(_media, "owner-recording-1a.mp4"), [1, 2, 3]);
        var project = OwnVideoAssembling("Now it goes into a hot oven.");
        var controlPlane = Holding(project);

        await Run(controlPlane, project, new FakeProcessRunner());

        var package = controlPlane.Saves.ShouldHaveSingleItem().Project.Package.ShouldNotBeNull();
        package.Description.ShouldBe("How I bake.\n\n0:00 Getting started\n0:50 Into the oven\n1:00 Out of the oven");
    }
}
