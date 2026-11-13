using System.Text.Json;
using Scout.ControlPlane.Operator;
using Shouldly;
using Xunit;

namespace Scout.ControlPlane.Tests.Operator;

/// <summary>The owner's own video: the recording the owner attaches is the whole content, transcribed by the worker.</summary>
public sealed partial class OperatorServiceTests
{
    private const string RecordingName = "owner-recording-1a2b3c4d.mp4";
    private const string NoMediaInOwnVideo = "A project of your own video shows the recording in every scene; it takes no pictures or narration.";
    private const string NothingToMakeAgain = "A project of your own video has no narration or pictures to make again; edit the scene's text to change its captions.";

    private static Scene RecordingScene(string id, int order, int startMs, int endMs) =>
        new(id, order, $"Words of {id}.", string.Empty, false, "ready", null, "recording", "recording", null, null, Math.Max(0, endMs - startMs))
        {
            ClipStartMs = startMs,
            ClipEndMs = endMs,
        };

    private static Script Transcript(params ScriptSection[] sections) =>
        new(string.Empty, sections, string.Empty, [], string.Empty, [], []);

    private async Task<OperatorProject> WaitingForRecordingAsync()
    {
        await GrantYouTubeAsync();
        return await _operator.StartProjectAsync("A walk by the Thames", "own_video", string.Empty, None);
    }

    private async Task<OperatorProject> TranscribingAsync()
    {
        var project = await WaitingForRecordingAsync();
        await PutInMediaAsync(project.Id, RecordingName);
        await _operator.AttachSceneMediaAsync(project.Id, string.Empty, "recording", RecordingName, None);
        var taken = (await _operator.NextQueuedProjectAsync(None)).ShouldNotBeNull();
        taken.Status.ShouldBe("transcribing");
        return taken;
    }

    private async Task<OperatorProject> OwnVideoWithScenesAsync(string status, params Scene[] scenes)
    {
        var project = await WaitingForRecordingAsync();
        await SetStoredAsync(project.Id, p => p with
        {
            Status = status,
            RecordingFile = RecordingName,
            RecordingMs = 9000,
            Script = Transcript(new ScriptSection(string.Empty, "Words.") { StartMs = 0, EndMs = 9000 }),
            Scenes = scenes,
        });
        return await StoredAsync(project.Id);
    }

    [Fact]
    public async Task An_Own_Video_Project_Waits_For_Its_Recording_And_The_Worker_Takes_Nothing()
    {
        var started = await WaitingForRecordingAsync();
        var written = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(_store.ProjectsPath, written);
        var bytes = await File.ReadAllBytesAsync(_store.ProjectsPath);

        var next = await _operator.NextQueuedProjectAsync(None);

        (started.Status, started.Format, started.RecordingFile).ShouldBe(("recording_waiting", "own_video", (string?)null));
        var stored = await StoredAsync(started.Id);
        (stored.Status, stored.Format, stored.RecordingFile, stored.RecordingMs).ShouldBe(("recording_waiting", "own_video", (string?)null, 0));
        next.ShouldBeNull();
        (await File.ReadAllBytesAsync(_store.ProjectsPath)).ShouldBe(bytes);
        File.GetLastWriteTimeUtc(_store.ProjectsPath).ShouldBe(written);
    }

    [Fact]
    public async Task Attaching_The_Recording_Queues_The_Project_And_The_Worker_Takes_It_As_Transcribing()
    {
        var project = await WaitingForRecordingAsync();
        await PutInMediaAsync(project.Id, RecordingName);
        await RevokeYouTubeAsync();
        var otherFiles = FilesOtherThanProjectsAndWriteLog();
        var logged = await _writes.ListAsync(500, None);

        var attached = await _operator.AttachSceneMediaAsync(project.Id, string.Empty, "recording", RecordingName, None);

        (attached.Status, attached.RecordingFile, attached.RecordingMs).ShouldBe(("queued", (string?)RecordingName, 0));
        var stored = await StoredAsync(project.Id);
        (stored.Status, stored.RecordingFile).ShouldBe(("queued", (string?)RecordingName));
        (await _writes.ListAsync(500, None)).ShouldBe(logged);
        FilesOtherThanProjectsAndWriteLog().ShouldBe(otherFiles);
        await NothingQueuedForTheOwnerAsync();

        await GrantYouTubeAsync();
        var taken = (await _operator.NextQueuedProjectAsync(None)).ShouldNotBeNull();

        (taken.Id, taken.Status, taken.RecordingFile).ShouldBe((project.Id, "transcribing", (string?)RecordingName));
        (await StoredAsync(project.Id)).Status.ShouldBe("transcribing");
        (await _writes.ListAsync(1, None)).ShouldHaveSingleItem().Outcome.ShouldBe("saved transcribing");
    }

    [Fact]
    public async Task A_Recording_Is_Refused_On_A_Scene_On_Another_Format_With_Another_Extension_Or_Not_In_The_Media_Folder()
    {
        var project = await WaitingForRecordingAsync();
        var pictures = await _operator.StartProjectAsync("Why rivers meander", "own_pictures", string.Empty, None);
        await PutInMediaAsync(project.Id, RecordingName);
        await PutInMediaAsync(project.Id, "owner-recording-talk.avi");
        await PutInMediaAsync(pictures.Id, RecordingName);
        var before = JsonSerializer.Serialize(await _store.ReadProjectsAsync(None));

        var onAScene = await Should.ThrowAsync<OperatorValidationException>(() =>
            _operator.AttachSceneMediaAsync(project.Id, "s1", "recording", RecordingName, None));
        var otherFormat = await Should.ThrowAsync<OperatorValidationException>(() =>
            _operator.AttachSceneMediaAsync(pictures.Id, string.Empty, "recording", RecordingName, None));
        var otherExtension = await Should.ThrowAsync<OperatorValidationException>(() =>
            _operator.AttachSceneMediaAsync(project.Id, string.Empty, "recording", "owner-recording-talk.avi", None));
        var notCopied = await Should.ThrowAsync<OperatorValidationException>(() =>
            _operator.AttachSceneMediaAsync(project.Id, string.Empty, "recording", "owner-recording-talk.mov", None));
        var afterRefusals = JsonSerializer.Serialize(await _store.ReadProjectsAsync(None));

        onAScene.Message.ShouldBe("A recording is attached to the project, not to a scene.");
        otherFormat.Message.ShouldBe("Only a project of your own video takes a recording.");
        otherExtension.Message.ShouldBe(
            "'owner-recording-talk.avi' is not a recording file Scout accepts: a plain file name of at most 120 characters, without a folder or a leading dot, ending in .mp4, .mkv, .webm, .mov.");
        notCopied.Message.ShouldBe("'owner-recording-talk.mov' is not in the project's media folder; copy it there first.");
        afterRefusals.ShouldBe(before);

        foreach (var name in new[] { "owner-recording-talk.MKV", "owner-recording-talk.webm", "owner-recording-talk.mov" })
        {
            await PutInMediaAsync(project.Id, name);
            (await _operator.AttachSceneMediaAsync(project.Id, string.Empty, "recording", name, None)).RecordingFile.ShouldBe(name);
        }

        (await StoredAsync(project.Id)).RecordingFile.ShouldBe("owner-recording-talk.mov");
    }

    [Fact]
    public async Task A_Recording_Is_Refused_Once_The_Project_Has_Its_Transcript()
    {
        var project = await TranscribingAsync();
        await PutInMediaAsync(project.Id, "owner-recording-again.mp4");
        var refusals = new List<string>();
        foreach (var (status, withScript) in new[] { ("transcribing", false), ("script_ready", true), ("producing", true), ("failed", true) })
        {
            await SetStoredAsync(project.Id, p => p with
            {
                Status = status,
                Script = withScript ? Transcript(new ScriptSection(string.Empty, "Words.") { StartMs = 0, EndMs = 9000 }) : null,
            });
            var refused = (await Should.ThrowAsync<OperatorValidationException>(() =>
                _operator.AttachSceneMediaAsync(project.Id, string.Empty, "recording", "owner-recording-again.mp4", None))).Message;
            refusals.Add($"{refused} | {(await StoredAsync(project.Id)).RecordingFile}");
        }

        await SetStoredAsync(project.Id, p => p with { Status = "failed", Script = null, Error = "Scout could not transcribe the recording." });
        var again = await _operator.AttachSceneMediaAsync(project.Id, string.Empty, "recording", "owner-recording-again.mp4", None);

        refusals.ShouldBe([
            $"A recording is attached before Scout transcribes it; this one is transcribing. | {RecordingName}",
            $"A recording is attached before Scout transcribes it; this one is script_ready. | {RecordingName}",
            $"A recording is attached before Scout transcribes it; this one is producing. | {RecordingName}",
            $"A recording is attached before Scout transcribes it; this one is failed. | {RecordingName}",
        ]);
        (again.Status, again.RecordingFile, again.Error).ShouldBe(("queued", (string?)"owner-recording-again.mp4", (string?)null));
    }

    [Fact]
    public async Task A_Worker_Save_While_Transcribing_Keeps_The_Owners_Recording_And_Takes_Its_Length()
    {
        var taken = await TranscribingAsync();
        var sections = Transcript(
            new ScriptSection(string.Empty, "Hello from the river.") { StartMs = 0, EndMs = 4200 },
            new ScriptSection(string.Empty, "Now the bend.") { StartMs = 4200, EndMs = 9000 });

        var progress = await _operator.SaveProjectFromWorkerAsync(
            taken with { RecordingFile = "elsewhere.mp4", RecordingMs = 9000, Format = "generated_pictures", Script = sections }, null, None);
        var ready = await _operator.SaveProjectFromWorkerAsync(
            progress with { Status = "script_ready", RecordingFile = null, RecordingMs = 9000 }, null, None);
        await _operator.ProduceProjectAsync(taken.Id, string.Empty, string.Empty, string.Empty, None);
        var producing = (await _operator.NextQueuedProjectAsync(None)).ShouldNotBeNull();
        var planned = await _operator.SaveProjectFromWorkerAsync(
            producing with { Status = "media_ready", RecordingFile = "elsewhere.mp4", RecordingMs = 1, Scenes = [RecordingScene("s1", 1, 0, 9000)] }, null, None);

        (progress.Status, progress.RecordingFile, progress.RecordingMs, progress.Format).ShouldBe(("transcribing", (string?)RecordingName, 9000, "own_video"));
        (ready.Status, ready.RecordingFile, ready.RecordingMs).ShouldBe(("script_ready", (string?)RecordingName, 9000));
        (producing.Status, producing.RecordingMs).ShouldBe(("producing", 9000));
        (planned.Status, planned.RecordingFile, planned.RecordingMs).ShouldBe(("media_ready", (string?)RecordingName, 9000));
        var stored = await StoredAsync(taken.Id);
        (stored.RecordingFile, stored.RecordingMs, stored.Format).ShouldBe(((string?)RecordingName, 9000, "own_video"));
        stored.Script.ShouldNotBeNull().Sections.Select(s => (s.Narration, s.StartMs, s.EndMs))
            .ShouldBe([("Hello from the river.", 0, 4200), ("Now the bend.", 4200, 9000)]);
        (await _writes.ListAsync(10, None)).Select(e => e.Outcome).Take(4)
            .ShouldBe(["saved media_ready", "saved producing", "saved script_ready", "saved transcribing"]);
    }

    [Fact]
    public async Task A_Stale_Transcribing_Project_Is_Taken_Again_As_Transcribing()
    {
        await GrantYouTubeAsync();
        var now = DateTimeOffset.UtcNow;
        OperatorProject Stored(string id, string status, TimeSpan idle) =>
            new OperatorProject(id, $"Topic {id}", status, "desktop", now.AddHours(-3), now - idle, [], null, [])
            {
                Format = "own_video",
                RecordingFile = RecordingName,
            };
        await _store.UpdateProjectsAsync(
            projects =>
            {
                projects.Add(Stored("waiting", "recording_waiting", TimeSpan.FromHours(2)));
                projects.Add(Stored("live", "transcribing", TimeSpan.FromMinutes(2)));
                projects.Add(Stored("stuck", "transcribing", TimeSpan.FromMinutes(20)));
                return 0;
            },
            None);

        var first = await _operator.NextQueuedProjectAsync(None);
        var second = await _operator.NextQueuedProjectAsync(None);

        var taken = first.ShouldNotBeNull();
        (taken.Id, taken.Status, taken.RecordingFile).ShouldBe(("stuck", "transcribing", (string?)RecordingName));
        taken.UpdatedAt.ShouldBeInRange(now, DateTimeOffset.UtcNow);
        second.ShouldBeNull();
        (await StoredAsync("live")).UpdatedAt.ShouldBe(now - TimeSpan.FromMinutes(2));
        (await StoredAsync("waiting")).Status.ShouldBe("recording_waiting");
    }

    [Fact]
    public async Task A_Transcribing_Project_Cannot_Be_Deleted()
    {
        var project = await TranscribingAsync();
        var media = await _operator.GetMediaDirectoryAsync(project.Id, None);

        var ex = await Should.ThrowAsync<OperatorValidationException>(() => _operator.DeleteProjectAsync(project.Id, None));

        ex.Message.ShouldBe("A project that is researching, scripting, transcribing, producing, assembling, presenting or uploading cannot be deleted; wait for it to finish or fail.");
        (await StoredAsync(project.Id)).Status.ShouldBe("transcribing");
        File.Exists(Path.Combine(media, RecordingName)).ShouldBeTrue();
    }

    [Fact]
    public async Task Editing_An_Own_Video_Scenes_Text_Changes_Only_Its_Captions()
    {
        var first = RecordingScene("s1", 1, 0, 4200);
        var second = RecordingScene("s2", 2, 4200, 9000);
        var assembled = await OwnVideoWithScenesAsync("approved", first, second);
        await SetStoredAsync(assembled.Id, p => p with
        {
            Package = new VideoPackage("approved", "video.mp4", [], null, [], true, DateTimeOffset.UnixEpoch, "Title", "Description", [], 9000, null, false),
        });
        var mediaReady = await OwnVideoWithScenesAsync("media_ready", first, second with { Locked = true });
        await RevokeYouTubeAsync();
        var logged = await _writes.ListAsync(500, None);

        var edited = await _operator.EditSceneTextAsync(assembled.Id, "s1", "  Hello from\tthe river.  ", None);
        var stillReady = await _operator.EditSceneTextAsync(mediaReady.Id, "s1", "Hello again.", None);
        var locked = await Should.ThrowAsync<OperatorValidationException>(() => _operator.EditSceneTextAsync(mediaReady.Id, "s2", "Words.", None));
        var tooLong = await Should.ThrowAsync<OperatorValidationException>(() => _operator.EditSceneTextAsync(mediaReady.Id, "s1", new string('a', 701), None));

        edited.Status.ShouldBe("media_ready");
        var stored = await StoredAsync(assembled.Id);
        stored.Status.ShouldBe("media_ready");
        stored.Scenes.ShouldBe([first with { Narration = "Hello from the river." }, second]);
        var package = stored.Package.ShouldNotBeNull();
        (package.Status, package.RightsConfirmed, package.ApprovedAt).ShouldBe(("assembled", false, (DateTimeOffset?)null));
        stillReady.Status.ShouldBe("media_ready");
        (await StoredAsync(mediaReady.Id)).Scenes.ShouldBe([first with { Narration = "Hello again." }, second with { Locked = true }]);
        locked.Message.ShouldBe("Scene 's2' is locked; unlock it before editing its text.");
        tooLong.Message.ShouldBe("A scene's text is 1 to 700 characters.");
        (await _writes.ListAsync(500, None)).ShouldBe(logged);
    }

    [Fact]
    public async Task An_Own_Video_Project_Refuses_Attached_Pictures_Narration_And_Regeneration()
    {
        var project = await OwnVideoWithScenesAsync("media_ready", RecordingScene("s1", 1, 0, 4200), RecordingScene("s2", 2, 4200, 9000));
        await PutInMediaAsync(project.Id, "mine.png");
        await PutInMediaAsync(project.Id, "mine.mp3");
        var before = JsonSerializer.Serialize(await StoredAsync(project.Id));

        var picture = await Should.ThrowAsync<OperatorValidationException>(() => _operator.AttachSceneMediaAsync(project.Id, "s1", "visual", "mine.png", None));
        var narration = await Should.ThrowAsync<OperatorValidationException>(() => _operator.AttachSceneMediaAsync(project.Id, "s1", "audio", "mine.mp3", None));
        var scene = await Should.ThrowAsync<OperatorValidationException>(() => _operator.RegenerateSceneAsync(project.Id, "s1", None));
        var voice = await Should.ThrowAsync<OperatorValidationException>(() => _operator.RegenerateNarrationAsync(project.Id, "s1", None));
        var afterRefusals = JsonSerializer.Serialize(await StoredAsync(project.Id));
        await _operator.SetSceneLockedAsync(project.Id, "s2", true, None);
        var reordered = await _operator.ReorderScenesAsync(project.Id, ["s2", "s1"], None);

        (picture.Message, narration.Message).ShouldBe((NoMediaInOwnVideo, NoMediaInOwnVideo));
        (scene.Message, voice.Message).ShouldBe((NothingToMakeAgain, NothingToMakeAgain));
        afterRefusals.ShouldBe(before);
        reordered.Scenes.Select(s => (s.Id, s.Order, s.Locked)).ShouldBe([("s2", 1, true), ("s1", 2, false)]);
    }

    [Fact]
    public async Task A_Recording_Scene_Needs_A_Clip_That_Ends_After_It_Starts()
    {
        var taken = await TranscribingAsync();
        await _operator.SaveProjectFromWorkerAsync(
            taken with { Status = "script_ready", Script = Transcript(new ScriptSection(string.Empty, "Words.") { StartMs = 0, EndMs = 9000 }) }, null, None);
        await _operator.ProduceProjectAsync(taken.Id, string.Empty, string.Empty, string.Empty, None);
        var producing = (await _operator.NextQueuedProjectAsync(None)).ShouldNotBeNull();
        Scene[] broken =
        [
            RecordingScene("s1", 1, 0, 0),
            RecordingScene("s1", 1, 5000, 4000),
            RecordingScene("s1", 1, -1, 4000),
            RecordingScene("s1", 1, 0, 0) with { VisualSource = "text_card" },
        ];

        var refusals = new List<string>();
        foreach (var scene in broken)
        {
            refusals.Add((await Should.ThrowAsync<OperatorValidationException>(() =>
                _operator.SaveProjectFromWorkerAsync(producing with { Status = "media_ready", Scenes = [scene] }, null, None))).Message);
        }

        var afterRefusals = await StoredAsync(taken.Id);
        var saved = await _operator.SaveProjectFromWorkerAsync(
            producing with { Status = "media_ready", Scenes = [RecordingScene("s1", 1, 0, 4200), RecordingScene("s2", 2, 4200, 9000)] }, null, None);

        refusals.ShouldBe(Enumerable.Repeat("Scene 's1' shows the recording; it needs a clip that ends after it starts.", 4));
        (afterRefusals.Status, afterRefusals.Scenes.Count).ShouldBe(("producing", 0));
        saved.Scenes.Select(s => (s.Id, s.AudioSource, s.VisualSource, s.ClipStartMs, s.ClipEndMs))
            .ShouldBe([("s1", "recording", "recording", 0, 4200), ("s2", "recording", "recording", 4200, 9000)]);
    }

    [Fact]
    public async Task Replacing_The_Recording_Deletes_The_One_It_Replaces_From_The_Media_Folder()
    {
        var project = await WaitingForRecordingAsync();
        var media = await _operator.GetMediaDirectoryAsync(project.Id, None);
        await PutInMediaAsync(project.Id, "owner-recording-aaaa.mp4");
        await PutInMediaAsync(project.Id, "owner-recording-bbbb.mov");
        await PutInMediaAsync(project.Id, "owner-s1-cccc.png");

        await _operator.AttachSceneMediaAsync(project.Id, string.Empty, "recording", "owner-recording-aaaa.mp4", None);
        var replaced = await _operator.AttachSceneMediaAsync(project.Id, string.Empty, "recording", "owner-recording-bbbb.mov", None);
        File.Delete(Path.Combine(media, "owner-recording-bbbb.mov"));
        await PutInMediaAsync(project.Id, "owner-recording-dddd.mp4");
        var afterAMissingOne = await _operator.AttachSceneMediaAsync(project.Id, string.Empty, "recording", "owner-recording-dddd.mp4", None);

        replaced.RecordingFile.ShouldBe("owner-recording-bbbb.mov");
        afterAMissingOne.RecordingFile.ShouldBe("owner-recording-dddd.mp4");
        Directory.GetFiles(media).Select(Path.GetFileName).Order().ShouldBe(["owner-recording-dddd.mp4", "owner-s1-cccc.png"]);
    }

    [Fact]
    public async Task A_Recording_Not_Named_By_Scout_Desktop_Is_Refused()
    {
        var project = await WaitingForRecordingAsync();
        await PutInMediaAsync(project.Id, "video.mp4");
        var before = JsonSerializer.Serialize(await StoredAsync(project.Id));

        var refused = await Should.ThrowAsync<OperatorValidationException>(() =>
            _operator.AttachSceneMediaAsync(project.Id, string.Empty, "recording", "video.mp4", None));

        refused.Message.ShouldBe("A recording is attached from Scout Desktop, which names the copy for you.");
        JsonSerializer.Serialize(await StoredAsync(project.Id)).ShouldBe(before);
    }

    [Fact]
    public async Task An_Own_Video_Project_Refuses_A_Background()
    {
        var project = await OwnVideoWithScenesAsync("media_ready", RecordingScene("s1", 1, 0, 9000));
        await PutInMediaAsync(project.Id, "owner-background-1a.png");
        var before = JsonSerializer.Serialize(await StoredAsync(project.Id));

        var refused = await Should.ThrowAsync<OperatorValidationException>(() =>
            _operator.AttachSceneMediaAsync(project.Id, string.Empty, "background", "owner-background-1a.png", None));

        refused.Message.ShouldBe(NoMediaInOwnVideo);
        JsonSerializer.Serialize(await StoredAsync(project.Id)).ShouldBe(before);
    }

    [Fact]
    public async Task A_Worker_Transcript_Save_On_A_Project_No_Longer_Transcribing_Is_Refused_And_Changes_Nothing()
    {
        var taken = await TranscribingAsync();
        await SetStoredAsync(taken.Id, p => p with { Status = "failed", Error = "Scout stopped transcribing the recording after 70 minutes." });
        var before = JsonSerializer.Serialize(await StoredAsync(taken.Id));

        var refused = await Should.ThrowAsync<OperatorTamperingException>(() => _operator.SaveProjectFromWorkerAsync(
            taken with { RecordingMs = 9000, Script = Transcript(new ScriptSection(string.Empty, "Late words.") { StartMs = 0, EndMs = 9000 }) }, null, None));

        refused.Message.ShouldBe("The project is no longer transcribing; the worker's transcript was not saved.");
        JsonSerializer.Serialize(await StoredAsync(taken.Id)).ShouldBe(before);
    }

    [Theory]
    [InlineData(true, "assembly_queued", "assembly_queued")]
    [InlineData(false, "media_ready", "assembled")]
    public async Task A_Caption_Edit_On_An_Assembled_Own_Video_Goes_Back_To_Assembly_Only_When_The_Owner_Delegated_It(
        bool delegated, string status, string packageStatus)
    {
        var project = await OwnVideoWithScenesAsync("assembled", RecordingScene("s1", 1, 0, 9000));
        await SetStoredAsync(project.Id, p => p with
        {
            Delegated = delegated,
            Package = new VideoPackage("assembled", "video.mp4", [], null, [], true, null, "Title", "Description", [], 9000, null, false),
        });

        var edited = await _operator.EditSceneTextAsync(project.Id, "s1", "New captions.", None);

        var stored = await StoredAsync(project.Id);
        (edited.Status, stored.Status, stored.Package?.Status, stored.Package?.RightsConfirmed).ShouldBe((status, status, (string?)packageStatus, (bool?)false));
        stored.Scenes.ShouldHaveSingleItem().Narration.ShouldBe("New captions.");
    }
}
