using System.Text.Json;
using Scout.ControlPlane.Operator;
using Shouldly;
using Xunit;

namespace Scout.ControlPlane.Tests.Operator;

/// <summary>The animated character: one presenter picture per project, the mouth the owner marks on it, and no scene pictures.</summary>
public sealed partial class OperatorServiceTests
{
    private const string PresenterName = "owner-presenter-0123456789ab.png";
    private const string NotACharacter = "The project is not an animated character.";
    private const string MouthBoxRule = "A mouth box is four fractions of the picture, each between 0 and 1, at least 0.02 wide and high and inside the picture.";

    private static Scene PresenterScene(string id, int order) =>
        new(id, order, $"Narration {id}", string.Empty, false, "ready", null, "generated", "presenter", $"{id}.mp3", null, 3000);

    private static VideoPackage ApprovedPackage() =>
        new("approved", "video.mp4", [], null, [], true, DateTimeOffset.UnixEpoch, "Title", "Description", [], 6000, null, false);

    private async Task<OperatorProject> CharacterAsync(string status, params Scene[] scenes)
    {
        await GrantYouTubeAsync();
        var started = await _operator.StartProjectAsync("Why rivers meander", "animated_character", string.Empty, "A friendly fox.", None);
        await SetStoredAsync(started.Id, p => p with
        {
            Status = status,
            Script = new Script("Hook", [new ScriptSection("Bends", "Rivers bend.")], "Subscribe", [], string.Empty, [], []),
            Scenes = scenes,
            Package = status is "assembled" or "approved" ? ApprovedPackage() with { Status = status } : null,
        });
        return await StoredAsync(started.Id);
    }

    [Fact]
    public async Task A_Project_Starts_As_An_Animated_Character_With_Its_Presenter_Description_And_Keeps_Both()
    {
        await GrantYouTubeAsync();

        var started = await _operator.StartProjectAsync("Why rivers meander", " animated_character ", "Calm.", "  A friendly fox\tin a library.  ", None);
        var taken = (await _operator.NextQueuedProjectAsync(None)).ShouldNotBeNull();
        var saved = await _operator.SaveProjectFromWorkerAsync(
            Draft(taken, "script_ready") with { Format = "generated_pictures", Presenter = "A robot." }, null, None);
        var producedBlank = await _operator.ProduceProjectAsync(started.Id, string.Empty, "Calm.", string.Empty, string.Empty, None);
        await SetStoredAsync(started.Id, p => p with { Status = "script_ready" });
        var delegatedBlank = await _operator.DelegateProjectAsync(started.Id, string.Empty, "Calm.", string.Empty, "   ", None);
        await SetStoredAsync(started.Id, p => p with { Status = "script_ready" });
        var producedNew = await _operator.ProduceProjectAsync(started.Id, string.Empty, "Calm.", string.Empty, " A fox in a park. ", None);

        (started.Status, started.Format, started.Presenter).ShouldBe(("queued", "animated_character", "A friendly fox in a library."));
        (saved.Format, saved.Presenter).ShouldBe(("animated_character", "A friendly fox in a library."));
        (producedBlank.Presenter, delegatedBlank.Presenter).ShouldBe(("A friendly fox in a library.", "A friendly fox in a library."));
        producedNew.Presenter.ShouldBe("A fox in a park.");
        var stored = await StoredAsync(started.Id);
        (stored.Format, stored.Presenter).ShouldBe(("animated_character", "A fox in a park."));
    }

    [Fact]
    public async Task A_Presenter_Description_Over_300_Characters_Is_Refused_With_The_Sentence()
    {
        await GrantYouTubeAsync();
        var logged = await _writes.ListAsync(10, None);

        var atStart = await Should.ThrowAsync<OperatorValidationException>(() =>
            _operator.StartProjectAsync("Why rivers meander", "animated_character", string.Empty, new string('a', 301), None));
        var storedAfterStart = await _store.ReadProjectsAsync(None);
        var loggedAfterStart = await _writes.ListAsync(10, None);
        var project = await CharacterAsync("script_ready");
        var before = JsonSerializer.Serialize(await StoredAsync(project.Id));
        var onProduce = await Should.ThrowAsync<OperatorValidationException>(() =>
            _operator.ProduceProjectAsync(project.Id, string.Empty, string.Empty, string.Empty, new string('a', 301), None));
        var onDelegate = await Should.ThrowAsync<OperatorValidationException>(() =>
            _operator.DelegateProjectAsync(project.Id, string.Empty, string.Empty, string.Empty, new string('a', 301), None));
        var afterRefusals = JsonSerializer.Serialize(await StoredAsync(project.Id));
        var longest = await _operator.ProduceProjectAsync(project.Id, string.Empty, string.Empty, string.Empty, new string('a', 300), None);

        (atStart.Message, onProduce.Message, onDelegate.Message).ShouldBe((
            "A presenter description is at most 300 characters.",
            "A presenter description is at most 300 characters.",
            "A presenter description is at most 300 characters."));
        storedAfterStart.ShouldBeEmpty();
        loggedAfterStart.ShouldBe(logged);
        afterRefusals.ShouldBe(before);
        longest.Presenter.ShouldBe(new string('a', 300));
    }

    [Fact]
    public async Task A_Presenter_Picture_Is_Attached_To_The_Project_With_Scouts_Name_And_Replaces_And_Deletes_The_Previous_One()
    {
        var project = await CharacterAsync("approved", PresenterScene("s1", 1));
        var media = await _operator.GetMediaDirectoryAsync(project.Id, None);
        const string Second = "owner-presenter-ffffffffffff.webp";
        foreach (var name in new[] { PresenterName, Second, "presenter.png", "owner-presenter-0123456789a.png", "owner-presenter-0123456789ag.png", "owner-background-0123456789ab.png", "owner-s1-1a2b.png" })
        {
            await PutInMediaAsync(project.Id, name);
        }

        await RevokeYouTubeAsync();
        var logged = await _writes.ListAsync(500, None);
        var refusedNames = new List<string>();
        foreach (var name in new[] { "presenter.png", "owner-presenter-0123456789a.png", "owner-presenter-0123456789ag.png", "owner-background-0123456789ab.png" })
        {
            refusedNames.Add((await Should.ThrowAsync<OperatorValidationException>(() =>
                _operator.AttachSceneMediaAsync(project.Id, string.Empty, "presenter", name, None))).Message);
        }

        var afterRefusals = await StoredAsync(project.Id);
        var first = await _operator.AttachSceneMediaAsync(project.Id, string.Empty, "presenter", PresenterName, None);
        await _operator.SetMouthBoxAsync(project.Id, new MouthBox(0.4, 0.6, 0.2, 0.1), None);
        var replaced = await _operator.AttachSceneMediaAsync(project.Id, string.Empty, "presenter", Second, None);
        await _operator.SetMouthBoxAsync(project.Id, new MouthBox(0.3, 0.5, 0.2, 0.1), None);
        var again = await _operator.AttachSceneMediaAsync(project.Id, string.Empty, "presenter", Second, None);

        refusedNames.ShouldBe(Enumerable.Repeat("A presenter picture is attached from Scout Desktop, which names the copy for you.", 4));
        (afterRefusals.Status, afterRefusals.PresenterFile).ShouldBe(("approved", (string?)null));
        (first.Status, first.PresenterFile).ShouldBe(("media_ready", (string?)PresenterName));
        (replaced.PresenterFile, replaced.MouthBox).ShouldBe((Second, (MouthBox?)null));
        again.MouthBox.ShouldBe(new MouthBox(0.3, 0.5, 0.2, 0.1));
        var stored = await StoredAsync(project.Id);
        (stored.PresenterFile, stored.MouthBox).ShouldBe((Second, (MouthBox?)new MouthBox(0.3, 0.5, 0.2, 0.1)));
        var package = stored.Package.ShouldNotBeNull();
        (package.Status, package.RightsConfirmed, package.ApprovedAt).ShouldBe(("assembled", false, (DateTimeOffset?)null));
        File.Exists(Path.Combine(media, PresenterName)).ShouldBeFalse();
        File.Exists(Path.Combine(media, Second)).ShouldBeTrue();
        File.Exists(Path.Combine(media, "owner-s1-1a2b.png")).ShouldBeTrue();
        (await _writes.ListAsync(500, None)).ShouldBe(logged);
    }

    [Fact]
    public async Task A_Presenter_Picture_Is_Refused_Before_The_Script_Is_Ready_Or_Outside_The_Media_Folder()
    {
        var project = await CharacterAsync("producing");
        await PutInMediaAsync(project.Id, PresenterName);

        var producing = await Should.ThrowAsync<OperatorValidationException>(() =>
            _operator.AttachSceneMediaAsync(project.Id, string.Empty, "presenter", PresenterName, None));
        await SetStoredAsync(project.Id, p => p with { Status = "script_ready" });
        var notCopied = await Should.ThrowAsync<OperatorValidationException>(() =>
            _operator.AttachSceneMediaAsync(project.Id, string.Empty, "presenter", "owner-presenter-aaaaaaaaaaaa.jpg", None));

        producing.Message.ShouldBe(
            "A presenter picture is attached to a project whose script or media is ready, an assembled or approved one, one queued for assembly or assembling, or a failed one; this one is producing.");
        notCopied.Message.ShouldBe("'owner-presenter-aaaaaaaaaaaa.jpg' is not in the project's media folder; copy it there first.");
        (await StoredAsync(project.Id)).PresenterFile.ShouldBeNull();
    }

    [Fact]
    public async Task A_Presenter_Attach_On_Another_Format_Or_A_Scene_Is_Refused()
    {
        var character = await CharacterAsync("script_ready");
        var pictures = await ScriptReadyInAsync("generated_pictures");
        await PutInMediaAsync(character.Id, PresenterName);
        await PutInMediaAsync(pictures.Id, PresenterName);
        var before = JsonSerializer.Serialize(await _store.ReadProjectsAsync(None));

        var onAScene = await Should.ThrowAsync<OperatorValidationException>(() =>
            _operator.AttachSceneMediaAsync(character.Id, "s1", "presenter", PresenterName, None));
        var otherFormat = await Should.ThrowAsync<OperatorValidationException>(() =>
            _operator.AttachSceneMediaAsync(pictures.Id, string.Empty, "presenter", PresenterName, None));

        onAScene.Message.ShouldBe("A presenter picture is attached to the project, not to a scene.");
        otherFormat.Message.ShouldBe(NotACharacter);
        JsonSerializer.Serialize(await _store.ReadProjectsAsync(None)).ShouldBe(before);
    }

    [Fact]
    public async Task A_Mouth_Box_Is_Stored_And_Kept_On_A_Worker_Save()
    {
        await GrantYouTubeAsync();
        var started = await _operator.StartProjectAsync("Why rivers meander", "animated_character", string.Empty, "A friendly fox.", None);
        var taken = (await _operator.NextQueuedProjectAsync(None)).ShouldNotBeNull();
        var box = new MouthBox(0.4, 0.6, 0.2, 0.1);

        await SetStoredAsync(started.Id, p => p with { PresenterFile = PresenterName, MouthBox = box });
        var saved = await _operator.SaveProjectFromWorkerAsync(
            Draft(taken, "script_ready") with { MouthBox = new MouthBox(0, 0, 1, 1), Presenter = "A robot.", PresenterFile = "presenter-made.png" }, null, None);
        var cleared = await _operator.SaveProjectFromWorkerAsync(
            saved with { MouthBox = null, PresenterFile = null, Presenter = string.Empty }, null, None);
        var approved = await CharacterAsync("approved", PresenterScene("s1", 1));
        await SetStoredAsync(approved.Id, p => p with { MouthBox = box });
        var sameBox = await _operator.SetMouthBoxAsync(approved.Id, new MouthBox(0.4, 0.6, 0.2, 0.1), None);
        var moved = await _operator.SetMouthBoxAsync(approved.Id, new MouthBox(0.5, 0.5, 0.5, 0.5), None);

        (saved.MouthBox, saved.Presenter, saved.PresenterFile).ShouldBe((box, "A friendly fox.", (string?)PresenterName));
        (cleared.MouthBox, cleared.Presenter, cleared.PresenterFile).ShouldBe((box, "A friendly fox.", (string?)PresenterName));
        var stored = await StoredAsync(started.Id);
        (stored.MouthBox, stored.Presenter, stored.PresenterFile).ShouldBe((box, "A friendly fox.", (string?)PresenterName));
        (sameBox.Status, sameBox.Package.ShouldNotBeNull().RightsConfirmed).ShouldBe(("approved", true));
        (moved.Status, moved.MouthBox).ShouldBe(("media_ready", new MouthBox(0.5, 0.5, 0.5, 0.5)));
        var package = (await StoredAsync(approved.Id)).Package.ShouldNotBeNull();
        (package.Status, package.RightsConfirmed, package.ApprovedAt).ShouldBe(("assembled", false, (DateTimeOffset?)null));
    }

    [Theory]
    [InlineData(-0.1, 0.5, 0.2, 0.1)]
    [InlineData(0.5, 1.1, 0.2, 0.1)]
    [InlineData(0.9, 0.5, 0.2, 0.1)]
    [InlineData(0.5, 0.95, 0.2, 0.1)]
    [InlineData(0.5, 0.5, 0.01, 0.1)]
    [InlineData(0.5, 0.5, 0.2, 0.019)]
    [InlineData(double.NaN, 0.5, 0.2, 0.1)]
    [InlineData(0.5, 0.5, double.PositiveInfinity, 0.1)]
    public async Task A_Mouth_Box_Outside_The_Picture_Or_Too_Small_Is_Refused_With_The_Sentence(double x, double y, double width, double height)
    {
        var project = await CharacterAsync("script_ready");
        var before = JsonSerializer.Serialize(await StoredAsync(project.Id));

        var refused = await Should.ThrowAsync<OperatorValidationException>(() =>
            _operator.SetMouthBoxAsync(project.Id, new MouthBox(x, y, width, height), None));

        refused.Message.ShouldBe(MouthBoxRule);
        JsonSerializer.Serialize(await StoredAsync(project.Id)).ShouldBe(before);
    }

    [Fact]
    public async Task A_Missing_Mouth_Box_Or_One_On_Another_Format_Is_Refused_And_The_Smallest_And_Largest_Are_Taken()
    {
        var character = await CharacterAsync("script_ready");
        var pictures = await ScriptReadyInAsync("own_pictures");
        var before = JsonSerializer.Serialize(await StoredAsync(pictures.Id));

        var missing = await Should.ThrowAsync<OperatorValidationException>(() => _operator.SetMouthBoxAsync(character.Id, null, None));
        var otherFormat = await Should.ThrowAsync<OperatorValidationException>(() =>
            _operator.SetMouthBoxAsync(pictures.Id, new MouthBox(0.4, 0.6, 0.2, 0.1), None));
        var smallest = await _operator.SetMouthBoxAsync(character.Id, new MouthBox(0, 0, 0.02, 0.02), None);
        var largest = await _operator.SetMouthBoxAsync(character.Id, new MouthBox(0, 0, 1, 1), None);

        missing.Message.ShouldBe(MouthBoxRule);
        otherFormat.Message.ShouldBe(NotACharacter);
        JsonSerializer.Serialize(await StoredAsync(pictures.Id)).ShouldBe(before);
        smallest.MouthBox.ShouldBe(new MouthBox(0, 0, 0.02, 0.02));
        largest.MouthBox.ShouldBe(new MouthBox(0, 0, 1, 1));
    }

    [Fact]
    public async Task Regenerating_The_Presenter_Clears_The_Picture_Queues_Production_And_Takes_The_Approval_Back()
    {
        var approved = await CharacterAsync("approved", PresenterScene("s1", 1));
        var scripted = await CharacterAsync("script_ready");
        var forAssembly = await CharacterAsync("assembly_queued", PresenterScene("s1", 1));
        var pictures = await ScriptReadyInAsync("generated_pictures");
        var box = new MouthBox(0.4, 0.6, 0.2, 0.1);
        await SetStoredAsync(approved.Id, p => p with { PresenterFile = PresenterName, MouthBox = box });
        await SetStoredAsync(scripted.Id, p => p with { PresenterFile = PresenterName, MouthBox = box });
        await SetStoredAsync(forAssembly.Id, p => p with { PresenterFile = PresenterName, MouthBox = box });
        await RevokeYouTubeAsync();
        var logged = await _writes.ListAsync(500, None);

        var queued = await _operator.RegeneratePresenterAsync(approved.Id, None);
        var notProduced = await _operator.RegeneratePresenterAsync(scripted.Id, None);
        var notAssembled = await _operator.RegeneratePresenterAsync(forAssembly.Id, None);
        var otherFormat = await Should.ThrowAsync<OperatorValidationException>(() => _operator.RegeneratePresenterAsync(pictures.Id, None));

        (queued.Status, queued.PresenterFile, queued.MouthBox).ShouldBe(("production_queued", (string?)null, (MouthBox?)null));
        var stored = await StoredAsync(approved.Id);
        (stored.Status, stored.PresenterFile, stored.MouthBox).ShouldBe(("production_queued", (string?)null, (MouthBox?)null));
        var package = stored.Package.ShouldNotBeNull();
        (package.Status, package.RightsConfirmed, package.ApprovedAt).ShouldBe(("assembled", false, (DateTimeOffset?)null));
        (notProduced.Status, notProduced.PresenterFile, notProduced.MouthBox).ShouldBe(("script_ready", (string?)null, (MouthBox?)null));
        (notAssembled.Status, notAssembled.PresenterFile, notAssembled.MouthBox).ShouldBe(("production_queued", (string?)null, (MouthBox?)null));
        otherFormat.Message.ShouldBe(NotACharacter);
        (await _writes.ListAsync(500, None)).ShouldBe(logged);
    }

    [Fact]
    public async Task Regenerate_Scene_And_Attach_Image_Are_Refused_On_An_Animated_Character_Scene_While_Narration_And_Text_Stay_Editable()
    {
        var project = await CharacterAsync("media_ready", PresenterScene("s1", 1), PresenterScene("s2", 2));
        await PutInMediaAsync(project.Id, "mine.png");
        var before = JsonSerializer.Serialize(await StoredAsync(project.Id));

        var scene = await Should.ThrowAsync<OperatorValidationException>(() => _operator.RegenerateSceneAsync(project.Id, "s1", None));
        var picture = await Should.ThrowAsync<OperatorValidationException>(() =>
            _operator.AttachSceneMediaAsync(project.Id, "s1", "visual", "mine.png", None));
        var afterRefusals = JsonSerializer.Serialize(await StoredAsync(project.Id));
        var narration = await _operator.RegenerateNarrationAsync(project.Id, "s1", None);
        var text = await _operator.EditSceneTextAsync(project.Id, "s2", "The fox explains the bend.", None);

        scene.Message.ShouldBe("A scene of an animated character has no picture of its own; regenerate its narration or edit its text.");
        picture.Message.ShouldBe("An animated character project takes no scene picture; attach the presenter picture instead.");
        afterRefusals.ShouldBe(before);
        narration.Status.ShouldBe("production_queued");
        narration.Scenes[0].ShouldBe(PresenterScene("s1", 1) with { Status = "planned", AudioSource = "none", AudioFile = null, DurationMs = 0 });
        text.Scenes[1].ShouldBe(PresenterScene("s2", 2) with { Narration = "The fox explains the bend.", Status = "planned", AudioSource = "none", AudioFile = null, DurationMs = 0 });
    }

    [Fact]
    public async Task A_Worker_Save_Of_Presenter_Scenes_Is_Accepted_And_Records_The_Presenter_Picture_Only_While_None_Is_Set()
    {
        await GrantYouTubeAsync();
        var started = await _operator.StartProjectAsync("Why rivers meander", "animated_character", string.Empty, "A friendly fox.", None);
        await _operator.NextQueuedProjectAsync(None);
        await SetStoredAsync(started.Id, p => p with { Status = "script_ready", Script = new Script("Hook", [], "Subscribe", [], string.Empty, [], []) });
        await _operator.ProduceProjectAsync(started.Id, string.Empty, string.Empty, string.Empty, None);
        var producing = (await _operator.NextQueuedProjectAsync(None)).ShouldNotBeNull();
        var pictures = await ScriptReadyInAsync("generated_pictures");
        await PutInMediaAsync(started.Id, "presenter-1a2b.png");
        await PutInMediaAsync(pictures.Id, "presenter-1a2b.png");

        var badName = await Should.ThrowAsync<OperatorValidationException>(() => _operator.SaveProjectFromWorkerAsync(
            producing with { PresenterFile = "../presenter.png" }, null, None));
        var made = await _operator.SaveProjectFromWorkerAsync(
            producing with { PresenterFile = "presenter-1a2b.png", Scenes = [PresenterScene("s1", 1), PresenterScene("s2", 2)] }, null, None);
        var replaced = await _operator.SaveProjectFromWorkerAsync(
            made with { Status = "media_ready", PresenterFile = "presenter-3c4d.png" }, null, None);
        var otherFormat = await _operator.SaveProjectFromWorkerAsync(pictures with { PresenterFile = "presenter-1a2b.png" }, null, None);

        badName.Message.ShouldStartWith("'../presenter.png' is not a visual file Scout accepts");
        made.Scenes.Select(s => (s.Id, s.VisualSource, s.VisualFile)).ShouldBe([("s1", "presenter", (string?)null), ("s2", "presenter", (string?)null)]);
        made.PresenterFile.ShouldBe("presenter-1a2b.png");
        (replaced.Status, replaced.PresenterFile).ShouldBe(("media_ready", (string?)"presenter-1a2b.png"));
        (await StoredAsync(started.Id)).PresenterFile.ShouldBe("presenter-1a2b.png");
        otherFormat.PresenterFile.ShouldBeNull();
    }

    [Fact]
    public async Task A_Replaced_Presenter_Picture_That_Cannot_Be_Deleted_Leaves_The_New_One_Attached()
    {
        var project = await CharacterAsync("script_ready");
        var media = await _operator.GetMediaDirectoryAsync(project.Id, None);
        const string Stuck = "owner-presenter-aaaaaaaaaaaa.png";
        Directory.CreateDirectory(Path.Combine(media, Stuck));
        await SetStoredAsync(project.Id, p => p with { PresenterFile = Stuck });
        await PutInMediaAsync(project.Id, PresenterName);

        var attached = await _operator.AttachSceneMediaAsync(project.Id, string.Empty, "presenter", PresenterName, None);

        attached.PresenterFile.ShouldBe(PresenterName);
        (await StoredAsync(project.Id)).PresenterFile.ShouldBe(PresenterName);
        Directory.Exists(Path.Combine(media, Stuck)).ShouldBeTrue();
    }

    [Fact]
    public async Task Regenerating_The_Presenter_While_The_Project_Runs_Is_Refused()
    {
        var producing = await CharacterAsync("producing", PresenterScene("s1", 1));
        var assembling = await CharacterAsync("assembling", PresenterScene("s1", 1));
        foreach (var id in new[] { producing.Id, assembling.Id })
        {
            await SetStoredAsync(id, p => p with { PresenterFile = PresenterName });
            await PutInMediaAsync(id, PresenterName);
        }

        var before = JsonSerializer.Serialize(await _store.ReadProjectsAsync(None));
        var logged = await _writes.ListAsync(500, None);

        var whileProducing = await Should.ThrowAsync<OperatorValidationException>(() => _operator.RegeneratePresenterAsync(producing.Id, None));
        var whileAssembling = await Should.ThrowAsync<OperatorValidationException>(() => _operator.RegeneratePresenterAsync(assembling.Id, None));

        (whileProducing.Message, whileAssembling.Message).ShouldBe((
            "A project that is producing or assembling cannot have its presenter picture regenerated; wait for it to finish or fail.",
            "A project that is producing or assembling cannot have its presenter picture regenerated; wait for it to finish or fail."));
        JsonSerializer.Serialize(await _store.ReadProjectsAsync(None)).ShouldBe(before);
        (await StoredAsync(producing.Id)).PresenterFile.ShouldBe(PresenterName);
        (await StoredAsync(assembling.Id)).PresenterFile.ShouldBe(PresenterName);
        (await _writes.ListAsync(500, None)).ShouldBe(logged);
    }

    [Fact]
    public async Task Regenerating_The_Presenter_Deletes_The_Old_Picture_After_The_Store_Commit_And_Ignores_A_Delete_That_Fails()
    {
        var ready = await CharacterAsync("media_ready", PresenterScene("s1", 1));
        var stuck = await CharacterAsync("script_ready");
        var missing = await CharacterAsync("approved", PresenterScene("s1", 1));
        var escaping = await CharacterAsync("script_ready");
        var running = await CharacterAsync("producing", PresenterScene("s1", 1));
        var readyMedia = await _operator.GetMediaDirectoryAsync(ready.Id, None);
        var stuckMedia = await _operator.GetMediaDirectoryAsync(stuck.Id, None);
        var escapingMedia = await _operator.GetMediaDirectoryAsync(escaping.Id, None);
        var runningMedia = await _operator.GetMediaDirectoryAsync(running.Id, None);
        const string Stuck = "owner-presenter-aaaaaaaaaaaa.png";
        var outside = Path.Combine(Path.GetDirectoryName(escapingMedia)!, "outside.png");
        await PutInMediaAsync(ready.Id, PresenterName);
        await PutInMediaAsync(ready.Id, "scene-s1.mp3");
        Directory.CreateDirectory(Path.Combine(stuckMedia, Stuck));
        await File.WriteAllTextAsync(outside, "image");
        await PutInMediaAsync(running.Id, PresenterName);
        await SetStoredAsync(ready.Id, p => p with { PresenterFile = PresenterName });
        await SetStoredAsync(stuck.Id, p => p with { PresenterFile = Stuck });
        await SetStoredAsync(missing.Id, p => p with { PresenterFile = PresenterName });
        await SetStoredAsync(escaping.Id, p => p with { PresenterFile = "../outside.png" });
        await SetStoredAsync(running.Id, p => p with { PresenterFile = PresenterName });

        var deleted = await _operator.RegeneratePresenterAsync(ready.Id, None);
        var undeletable = await _operator.RegeneratePresenterAsync(stuck.Id, None);
        var gone = await _operator.RegeneratePresenterAsync(missing.Id, None);
        var kept = await _operator.RegeneratePresenterAsync(escaping.Id, None);
        await Should.ThrowAsync<OperatorValidationException>(() => _operator.RegeneratePresenterAsync(running.Id, None));

        (deleted.Status, deleted.PresenterFile).ShouldBe(("production_queued", (string?)null));
        File.Exists(Path.Combine(readyMedia, PresenterName)).ShouldBeFalse();
        File.Exists(Path.Combine(readyMedia, "scene-s1.mp3")).ShouldBeTrue();
        undeletable.PresenterFile.ShouldBeNull();
        (await StoredAsync(stuck.Id)).PresenterFile.ShouldBeNull();
        Directory.Exists(Path.Combine(stuckMedia, Stuck)).ShouldBeTrue();
        (gone.Status, gone.PresenterFile).ShouldBe(("production_queued", (string?)null));
        kept.PresenterFile.ShouldBeNull();
        File.Exists(outside).ShouldBeTrue();
        File.Exists(Path.Combine(runningMedia, PresenterName)).ShouldBeTrue();
    }

    [Theory]
    [InlineData("owner-background-0123456789ab.png", true)]
    [InlineData("thumbnail.png", true)]
    [InlineData("scene-s1.png", true)]
    [InlineData("presenter-1a2b.png", false)]
    public async Task A_Worker_Save_Records_A_Presenter_Picture_Only_When_It_Is_Named_Presenter_And_Is_In_The_Media_Folder(string name, bool onDisk)
    {
        await GrantYouTubeAsync();
        var started = await _operator.StartProjectAsync("Why rivers meander", "animated_character", string.Empty, "A friendly fox.", None);
        await _operator.NextQueuedProjectAsync(None);
        await SetStoredAsync(started.Id, p => p with { Status = "script_ready", Script = new Script("Hook", [], "Subscribe", [], string.Empty, [], []) });
        await _operator.ProduceProjectAsync(started.Id, string.Empty, string.Empty, string.Empty, None);
        var producing = (await _operator.NextQueuedProjectAsync(None)).ShouldNotBeNull();
        if (onDisk)
        {
            await PutInMediaAsync(started.Id, name);
        }

        var saved = await _operator.SaveProjectFromWorkerAsync(producing with { PresenterFile = name }, null, None);
        await PutInMediaAsync(started.Id, "presenter-5e6f.png");
        var made = await _operator.SaveProjectFromWorkerAsync(saved with { PresenterFile = "presenter-5e6f.png" }, null, None);

        saved.PresenterFile.ShouldBeNull();
        made.PresenterFile.ShouldBe("presenter-5e6f.png");
        (await StoredAsync(started.Id)).PresenterFile.ShouldBe("presenter-5e6f.png");
    }

    [Theory]
    [InlineData("queued")]
    [InlineData("researching")]
    [InlineData("producing")]
    [InlineData("production_queued")]
    [InlineData("assembling")]
    public async Task A_Mouth_Box_On_A_Project_That_Is_Not_Ready_Or_Is_Assembling_Is_Refused_With_The_Sentence(string status)
    {
        var project = await CharacterAsync(status, PresenterScene("s1", 1));
        var before = JsonSerializer.Serialize(await StoredAsync(project.Id));

        var refused = await Should.ThrowAsync<OperatorValidationException>(() =>
            _operator.SetMouthBoxAsync(project.Id, new MouthBox(0.4, 0.6, 0.2, 0.1), None));

        refused.Message.ShouldBe(
            $"A mouth box is set on a project whose script or media is ready, an assembled or approved one, one queued for assembly, or a failed one; this one is {status}.");
        JsonSerializer.Serialize(await StoredAsync(project.Id)).ShouldBe(before);
    }

    [Theory]
    [InlineData("script_ready", "script_ready")]
    [InlineData("media_ready", "media_ready")]
    [InlineData("assembled", "media_ready")]
    [InlineData("approved", "media_ready")]
    [InlineData("assembly_queued", "assembly_queued")]
    [InlineData("failed", "failed")]
    public async Task A_Mouth_Box_Is_Set_On_A_Ready_Assembled_Approved_Queued_For_Assembly_Or_Failed_Project(string status, string after)
    {
        var project = await CharacterAsync(status, PresenterScene("s1", 1));

        var marked = await _operator.SetMouthBoxAsync(project.Id, new MouthBox(0.4, 0.6, 0.2, 0.1), None);

        (marked.Status, marked.MouthBox).ShouldBe((after, (MouthBox?)new MouthBox(0.4, 0.6, 0.2, 0.1)));
        (await StoredAsync(project.Id)).MouthBox.ShouldBe(new MouthBox(0.4, 0.6, 0.2, 0.1));
    }
}
