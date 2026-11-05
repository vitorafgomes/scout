using System.Text.Json;
using Scout.ControlPlane.Operator;
using Shouldly;
using Xunit;

namespace Scout.ControlPlane.Tests.Operator;

/// <summary>
/// The automatic presenter: the first one asked for when the script is ready, the presenter job handed out before
/// production, the owner's feedback and prompt, the versions, and a picture of its own on every scene.
/// </summary>
public sealed partial class OperatorServiceTests
{
    private const string FirstRequest = "first";
    private const string Fox = "presenter-builtin-fox.png";
    private const string RunningRefusal =
        "A presenter can be asked for on a project whose script or media is ready, one queued for production or assembly, an assembled or approved one, or a failed one with a script; this one is {0}.";
    private const string MakingPresenter = "The project is making its presenter; wait for it to finish.";
    private const string NoGeneratedPicture = "This format has no generated picture.";

    private static PresenterVersion MadeVersion(string file, string? preview, MouthBox? box = null, string source = "generated", string feedback = "") =>
        new(file, $"Prompt of {file}", feedback, preview, box, DateTimeOffset.UnixEpoch, source);

    private async Task<OperatorProject> ScriptReadyCharacterAsync()
    {
        await GrantYouTubeAsync();
        await _operator.StartProjectAsync("Why rivers meander", "animated_character", string.Empty, "A friendly fox.", None);
        var taken = (await _operator.NextQueuedProjectAsync(None)).ShouldNotBeNull();
        return await _operator.SaveProjectFromWorkerAsync(
            Draft(taken, "script_ready") with { Script = new Script("Hook. More.", [new ScriptSection("Bends", "Rivers bend.")], "Subscribe", [], string.Empty, [], []) },
            null,
            None);
    }

    private async Task<OperatorProject> PresentingAsync()
    {
        await ScriptReadyCharacterAsync();
        var taken = (await _operator.NextQueuedProjectAsync(None)).ShouldNotBeNull();
        taken.Status.ShouldBe("presenting");
        return taken;
    }

    private async Task<OperatorProject> WithVersionsAsync(string status, params PresenterVersion[] versions)
    {
        var project = await CharacterAsync(status, PresenterScene("s1", 1));
        foreach (var file in versions.SelectMany(v => new[] { v.File, v.PreviewFile }).OfType<string>().Distinct())
        {
            await PutInMediaAsync(project.Id, file);
        }

        var current = versions[^1];
        await SetStoredAsync(project.Id, p => p with
        {
            PresenterVersions = versions,
            PresenterFile = current.File,
            MouthBox = current.MouthBox,
            PresenterPreviewFile = current.PreviewFile,
            PresenterSource = current.Source,
        });
        return await StoredAsync(project.Id);
    }

    [Fact]
    public async Task A_Script_Ready_Animated_Character_Project_Asks_For_Its_First_Presenter_By_Itself()
    {
        var character = await ScriptReadyCharacterAsync();
        var pictures = await ScriptReadyInAsync("generated_pictures");
        await _operator.StartProjectAsync("Why the owl hoots", "animated_character", string.Empty, "A wise owl.", None);
        var attached = (await _operator.NextQueuedProjectAsync(None)).ShouldNotBeNull();
        await PutInMediaAsync(attached.Id, PresenterName);
        await SetStoredAsync(attached.Id, p => p with { PresenterFile = PresenterName });
        var withPicture = await _operator.SaveProjectFromWorkerAsync(Draft(attached, "script_ready"), null, None);
        await _operator.StartProjectAsync("Why the heron waits", "animated_character", string.Empty, "A patient heron.", None);
        var versioned = (await _operator.NextQueuedProjectAsync(None)).ShouldNotBeNull();
        await SetStoredAsync(versioned.Id, p => p with { PresenterVersions = [MadeVersion("presenter-1a2b.png", null)] });
        var withVersion = await _operator.SaveProjectFromWorkerAsync(Draft(versioned, "script_ready"), null, None);
        await _operator.StartProjectAsync("Why the crane dances", "animated_character", string.Empty, "A tall crane.", None);
        var pending = (await _operator.NextQueuedProjectAsync(None)).ShouldNotBeNull();
        await SetStoredAsync(pending.Id, p => p with { PresenterRequest = "Make the crane smile." });
        var alreadyAsked = await _operator.SaveProjectFromWorkerAsync(Draft(pending, "script_ready"), null, None);

        character.PresenterRequest.ShouldBe(FirstRequest);
        (await StoredAsync(character.Id)).PresenterRequest.ShouldBe(FirstRequest);
        (await StoredAsync(pictures.Id)).PresenterRequest.ShouldBeEmpty();
        withPicture.PresenterRequest.ShouldBeEmpty();
        (await StoredAsync(attached.Id)).PresenterRequest.ShouldBeEmpty();
        withVersion.PresenterRequest.ShouldBeEmpty();
        alreadyAsked.PresenterRequest.ShouldBe("Make the crane smile.");
    }

    [Fact]
    public async Task The_Presenter_Job_Is_Handed_Out_Before_Production_And_Its_Save_Restores_The_Status()
    {
        var waiting = await TakenForProductionAsync();
        await _operator.SaveProjectFromWorkerAsync(waiting with { Status = "media_ready", Scenes = [Scene("s1", 1)] }, null, None);
        await _operator.ProduceProjectAsync(waiting.Id, string.Empty, string.Empty, string.Empty, None);
        var character = await ScriptReadyCharacterAsync();
        await PutInMediaAsync(character.Id, "presenter-1a2b.png");
        await PutInMediaAsync(character.Id, "presenter-preview-1.mp4");
        var box = new MouthBox(0.4, 0.6, 0.2, 0.1);

        var presenting = (await _operator.NextQueuedProjectAsync(None)).ShouldNotBeNull();
        var storedWhilePresenting = await StoredAsync(character.Id);
        var production = (await _operator.NextQueuedProjectAsync(None)).ShouldNotBeNull();
        var before = DateTimeOffset.UtcNow;
        var saved = await _operator.SaveProjectFromWorkerAsync(
            presenting with { PresenterRequest = string.Empty, PresenterVersions = [MadeVersion("presenter-1a2b.png", "presenter-preview-1.mp4", box)] },
            null,
            None);

        (presenting.Id, presenting.Status).ShouldBe((character.Id, "presenting"));
        (storedWhilePresenting.Status, storedWhilePresenting.PresenterReturnStatus).ShouldBe(("presenting", "script_ready"));
        (production.Id, production.Status).ShouldBe((waiting.Id, "producing"));
        var stored = await StoredAsync(character.Id);
        (stored.Status, stored.PresenterRequest, stored.PresenterReturnStatus).ShouldBe(("script_ready", string.Empty, string.Empty));
        (stored.PresenterFile, stored.MouthBox, stored.PresenterPreviewFile, stored.PresenterSource)
            .ShouldBe(("presenter-1a2b.png", (MouthBox?)box, (string?)"presenter-preview-1.mp4", "generated"));
        var version = stored.PresenterVersions.ShouldHaveSingleItem();
        (version.File, version.PreviewFile, version.MouthBox, version.Source).ShouldBe(("presenter-1a2b.png", (string?)"presenter-preview-1.mp4", (MouthBox?)box, "generated"));
        version.MadeAt.ShouldBeInRange(before, DateTimeOffset.UtcNow);
        saved.Status.ShouldBe("script_ready");
    }

    [Fact]
    public async Task A_Presenter_Save_That_Keeps_The_Request_Or_Changes_The_Versions_Is_Refused_As_Tampering()
    {
        var presenting = await PresentingAsync();
        await PutInMediaAsync(presenting.Id, "presenter-1a2b.png");
        await PutInMediaAsync(presenting.Id, "presenter-3c4d.png");
        var versioned = await WithVersionsAsync("script_ready", MadeVersion("presenter-5e6f.png", null));
        await _operator.RequestPresenterAsync(versioned.Id, "Make the fox smile.", string.Empty, None);
        var again = (await _operator.NextQueuedProjectAsync(None)).ShouldNotBeNull();
        var before = JsonSerializer.Serialize(await StoredAsync(presenting.Id));
        var versionedBefore = JsonSerializer.Serialize(await StoredAsync(versioned.Id));

        var kept = await Should.ThrowAsync<OperatorTamperingException>(() => _operator.SaveProjectFromWorkerAsync(
            presenting with { PresenterVersions = [MadeVersion("presenter-1a2b.png", null)] }, null, None));
        var two = await Should.ThrowAsync<OperatorTamperingException>(() => _operator.SaveProjectFromWorkerAsync(
            presenting with { PresenterRequest = string.Empty, PresenterVersions = [MadeVersion("presenter-1a2b.png", null), MadeVersion("presenter-3c4d.png", null)] }, null, None));
        var notMade = await Should.ThrowAsync<OperatorValidationException>(() => _operator.SaveProjectFromWorkerAsync(
            presenting with { PresenterRequest = string.Empty, PresenterVersions = [MadeVersion("owner-presenter-0123456789ab.png", null)] }, null, None));
        var painted = await Should.ThrowAsync<OperatorValidationException>(() => _operator.SaveProjectFromWorkerAsync(
            presenting with { PresenterRequest = string.Empty, PresenterVersions = [MadeVersion("presenter-1a2b.png", null, source: "painted")] }, null, None));
        var outside = await Should.ThrowAsync<OperatorValidationException>(() => _operator.SaveProjectFromWorkerAsync(
            presenting with { PresenterRequest = string.Empty, PresenterVersions = [MadeVersion("presenter-1a2b.png", null, new MouthBox(0.9, 0.5, 0.5, 0.1))] }, null, None));
        var changed = await Should.ThrowAsync<OperatorTamperingException>(() => _operator.SaveProjectFromWorkerAsync(
            again with { PresenterRequest = string.Empty, PresenterVersions = [again.PresenterVersions[0] with { File = "presenter-1a2b.png" }] }, null, None));

        kept.Message.ShouldBe($"The worker may not save the presenter of project '{presenting.Id}' without clearing its presenter request.");
        two.Message.ShouldBe($"The worker may add one presenter version to project '{presenting.Id}' and change none it has.");
        notMade.Message.ShouldBe("A presenter version is a picture Scout made in the project's media folder, named presenter-.");
        painted.Message.ShouldBe("A presenter version is generated, builtin or owner.");
        outside.Message.ShouldBe("A mouth box is four fractions of the picture, each between 0 and 1, at least 0.02 wide and high and inside the picture.");
        changed.Message.ShouldBe($"The worker may add one presenter version to project '{versioned.Id}' and change none it has.");
        JsonSerializer.Serialize(await StoredAsync(presenting.Id)).ShouldBe(before);
        JsonSerializer.Serialize(await StoredAsync(versioned.Id)).ShouldBe(versionedBefore);
    }

    [Fact]
    public async Task A_Presenter_Save_Is_Refused_Without_The_Grant_Even_While_A_YouTube_Action_Runs()
    {
        var approved = await CharacterAsync("approved", PresenterScene("s1", 1));
        await PublishAsync(approved.Id);
        (await _operator.NextApprovedActionAsync(None)).ShouldNotBeNull().Status.ShouldBe(Scout.ControlPlane.Actions.PendingActionStatus.Executing);
        await _operator.RequestPresenterAsync(approved.Id, "Make the fox smile.", string.Empty, None);
        var presenting = (await _operator.NextQueuedProjectAsync(None)).ShouldNotBeNull();
        presenting.Status.ShouldBe("presenting");
        await PutInMediaAsync(approved.Id, "presenter-1a2b.png");
        await RevokeYouTubeAsync();
        var before = JsonSerializer.Serialize(await StoredAsync(approved.Id));
        var logged = (await _writes.ListAsync(500, None)).Count;

        await Should.ThrowAsync<OperatorWriteRefusedException>(() => _operator.SaveProjectFromWorkerAsync(
            presenting with { PresenterRequest = string.Empty, PresenterVersions = [MadeVersion("presenter-1a2b.png", null)] }, null, None));

        JsonSerializer.Serialize(await StoredAsync(approved.Id)).ShouldBe(before);
        (await StoredAsync(approved.Id)).PresenterVersions.ShouldBeEmpty();
        (await _writes.ListAsync(500, None)).Count.ShouldBe(logged);
    }

    [Fact]
    public async Task A_Presenter_Job_That_Made_Nothing_Restores_The_Status_And_A_Late_Presenting_Save_Is_Refused()
    {
        var presenting = await PresentingAsync();

        var nothing = await _operator.SaveProjectFromWorkerAsync(presenting with { PresenterRequest = string.Empty, Error = "No picture today." }, null, None);
        var late = await Should.ThrowAsync<OperatorTamperingException>(() =>
            _operator.SaveProjectFromWorkerAsync(presenting with { PresenterRequest = string.Empty }, null, None));

        (nothing.Status, nothing.PresenterRequest, nothing.PresenterVersions.Count, nothing.Error).ShouldBe(("script_ready", string.Empty, 0, (string?)"No picture today."));
        late.Message.ShouldBe("The project is no longer presenting.");
        (await StoredAsync(presenting.Id)).Status.ShouldBe("script_ready");
    }

    [Fact]
    public async Task A_Stale_Presenter_Job_Is_Handed_Out_Again_And_A_Running_Project_Is_Not_Taken_For_One()
    {
        var presenting = await PresentingAsync();
        var producing = await CharacterAsync("producing", PresenterScene("s1", 1));
        await SetStoredAsync(producing.Id, p => p with { PresenterRequest = "Make the fox smile." });

        var notYetStale = await _operator.NextQueuedProjectAsync(None);
        await SetStoredAsync(presenting.Id, p => p with { UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-16) });
        var again = (await _operator.NextQueuedProjectAsync(None)).ShouldNotBeNull();

        notYetStale.ShouldBeNull();
        (again.Id, again.Status, again.PresenterReturnStatus).ShouldBe((presenting.Id, "presenting", "script_ready"));
        (await StoredAsync(producing.Id)).Status.ShouldBe("producing");
    }

    [Fact]
    public async Task An_Automatic_Presenter_Never_Replaces_The_Owners_Picture_But_The_Owners_Request_Does()
    {
        var presenting = await PresentingAsync();
        await PutInMediaAsync(presenting.Id, PresenterName);
        await PutInMediaAsync(presenting.Id, "presenter-1a2b.png");
        await PutInMediaAsync(presenting.Id, "presenter-3c4d.png");
        var box = new MouthBox(0.4, 0.6, 0.2, 0.1);
        await SetStoredAsync(presenting.Id, p => p with { PresenterFile = PresenterName, MouthBox = box });

        var automatic = await _operator.SaveProjectFromWorkerAsync(
            presenting with { PresenterRequest = string.Empty, PresenterVersions = [MadeVersion("presenter-1a2b.png", null)] }, null, None);
        await _operator.RequestPresenterAsync(presenting.Id, "Make the fox smile.", string.Empty, None);
        var asked = (await _operator.NextQueuedProjectAsync(None)).ShouldNotBeNull();
        var owners = await _operator.SaveProjectFromWorkerAsync(
            asked with { PresenterRequest = string.Empty, PresenterVersions = [.. asked.PresenterVersions, MadeVersion("presenter-3c4d.png", null, source: "builtin")] }, null, None);

        (automatic.PresenterFile, automatic.MouthBox, automatic.PresenterVersions.Count).ShouldBe(((string?)PresenterName, (MouthBox?)box, 1));
        (owners.PresenterFile, owners.MouthBox, owners.PresenterSource, owners.PresenterVersions.Count)
            .ShouldBe(("presenter-3c4d.png", (MouthBox?)null, "builtin", 2));
    }

    [Fact]
    public async Task A_Worker_Save_Outside_The_Presenter_Job_Keeps_The_Presenter_Fields()
    {
        var project = await WithVersionsAsync("media_ready", MadeVersion("presenter-1a2b.png", "presenter-preview-1.mp4"));
        await SetStoredAsync(project.Id, p => p with { PresenterRequest = "Make the fox smile.", PresenterRequestPrompt = "A fox." });
        await _operator.ProduceProjectAsync(project.Id, string.Empty, string.Empty, string.Empty, None);
        await SetStoredAsync(project.Id, p => p with { Status = "producing" });
        var stored = await StoredAsync(project.Id);

        var saved = await _operator.SaveProjectFromWorkerAsync(
            stored with
            {
                PresenterVersions = [],
                PresenterRequest = string.Empty,
                PresenterRequestPrompt = string.Empty,
                PresenterPreviewFile = null,
                PresenterSource = "owner",
                PresenterReturnStatus = "approved",
            },
            null,
            None);

        (saved.PresenterVersions.Count, saved.PresenterRequest, saved.PresenterRequestPrompt, saved.PresenterPreviewFile, saved.PresenterSource, saved.PresenterReturnStatus)
            .ShouldBe((1, "Make the fox smile.", "A fox.", (string?)"presenter-preview-1.mp4", "generated", string.Empty));
    }

    [Fact]
    public async Task A_Presenter_Request_Stores_The_Feedback_And_Prompt_And_Takes_The_Approval_Back()
    {
        var approved = await CharacterAsync("approved", PresenterScene("s1", 1));
        var prompted = await CharacterAsync("script_ready");
        var another = await CharacterAsync("production_queued");
        var builtIn = await CharacterAsync("failed");
        var assembled = await CharacterAsync("assembled", PresenterScene("s1", 1));
        var assemblyQueued = await CharacterAsync("assembly_queued", PresenterScene("s1", 1));
        await RevokeYouTubeAsync();
        var logged = await _writes.ListAsync(500, None);

        var feedback = await _operator.RequestPresenterAsync(approved.Id, "  Make the fox\tsmile.  ", " A fox in a library, flat cartoon. ", None);
        var onlyPrompt = await _operator.RequestPresenterAsync(prompted.Id, string.Empty, "A fox in a library.", None);
        var neither = await _operator.RequestPresenterAsync(another.Id, " ", string.Empty, None);
        var picked = await _operator.RequestPresenterAsync(builtIn.Id, "builtin:fox-2_b", string.Empty, None);
        var onAssembled = await _operator.RequestPresenterAsync(assembled.Id, "Make the fox wave.", string.Empty, None);
        var onAssemblyQueued = await _operator.RequestPresenterAsync(assemblyQueued.Id, "Make the fox wave.", string.Empty, None);

        (feedback.Status, feedback.PresenterRequest, feedback.PresenterRequestPrompt).ShouldBe(("media_ready", "Make the fox smile.", "A fox in a library, flat cartoon."));
        var package = (await StoredAsync(approved.Id)).Package.ShouldNotBeNull();
        (package.Status, package.RightsConfirmed, package.ApprovedAt).ShouldBe(("assembled", false, (DateTimeOffset?)null));
        (onlyPrompt.Status, onlyPrompt.PresenterRequest, onlyPrompt.PresenterRequestPrompt).ShouldBe(("script_ready", "prompt", "A fox in a library."));
        (neither.Status, neither.PresenterRequest, neither.PresenterRequestPrompt).ShouldBe(("production_queued", "another", string.Empty));
        (picked.Status, picked.PresenterRequest).ShouldBe(("failed", "builtin:fox-2_b"));
        (onAssembled.Status, onAssembled.PresenterRequest).ShouldBe(("media_ready", "Make the fox wave."));
        (onAssemblyQueued.Status, onAssemblyQueued.PresenterRequest).ShouldBe(("assembly_queued", "Make the fox wave."));
        (await StoredAsync(approved.Id)).PresenterRequest.ShouldBe("Make the fox smile.");
        (await _writes.ListAsync(500, None)).ShouldBe(logged);
    }

    [Theory]
    [InlineData("queued", true)]
    [InlineData("researching", true)]
    [InlineData("scripting", true)]
    [InlineData("transcribing", true)]
    [InlineData("recording_waiting", true)]
    [InlineData("producing", true)]
    [InlineData("assembling", true)]
    [InlineData("presenting", true)]
    [InlineData("failed", false)]
    public async Task A_Presenter_Request_Is_Refused_While_The_Project_Runs_With_The_Sentence(string status, bool withScript)
    {
        var project = await CharacterAsync(status, PresenterScene("s1", 1));
        if (!withScript)
        {
            await SetStoredAsync(project.Id, p => p with { Script = null });
        }

        var before = JsonSerializer.Serialize(await StoredAsync(project.Id));

        var refused = await Should.ThrowAsync<OperatorValidationException>(() =>
            _operator.RequestPresenterAsync(project.Id, "Make the fox smile.", string.Empty, None));

        refused.Message.ShouldBe(string.Format(System.Globalization.CultureInfo.InvariantCulture, RunningRefusal, status));
        JsonSerializer.Serialize(await StoredAsync(project.Id)).ShouldBe(before);
    }

    [Fact]
    public async Task A_Presenter_Prompt_Over_1500_Characters_Is_Refused()
    {
        var project = await CharacterAsync("script_ready");
        var pictures = await ScriptReadyInAsync("generated_pictures");
        var before = JsonSerializer.Serialize(await _store.ReadProjectsAsync(None));

        var longPrompt = await Should.ThrowAsync<OperatorValidationException>(() =>
            _operator.RequestPresenterAsync(project.Id, string.Empty, new string('a', 1501), None));
        var longFeedback = await Should.ThrowAsync<OperatorValidationException>(() =>
            _operator.RequestPresenterAsync(project.Id, new string('a', 301), string.Empty, None));
        var badBuiltIn = await Should.ThrowAsync<OperatorValidationException>(() =>
            _operator.RequestPresenterAsync(project.Id, "builtin:../fox", string.Empty, None));
        var otherFormat = await Should.ThrowAsync<OperatorValidationException>(() =>
            _operator.RequestPresenterAsync(pictures.Id, "Make the fox smile.", string.Empty, None));
        var afterRefusals = JsonSerializer.Serialize(await _store.ReadProjectsAsync(None));
        var longest = await _operator.RequestPresenterAsync(project.Id, new string('b', 300), new string('a', 1500), None);

        longPrompt.Message.ShouldBe("A presenter prompt is at most 1500 characters.");
        longFeedback.Message.ShouldBe("Presenter feedback is at most 300 characters.");
        badBuiltIn.Message.ShouldBe("A built-in presenter is named by its id of letters, digits, - and _.");
        otherFormat.Message.ShouldBe(NotACharacter);
        afterRefusals.ShouldBe(before);
        (longest.PresenterRequest, longest.PresenterRequestPrompt).ShouldBe((new string('b', 300), new string('a', 1500)));
    }

    [Fact]
    public async Task A_Scene_Edit_That_Requeues_Production_Is_Refused_While_The_Project_Makes_Its_Presenter()
    {
        var project = await CharacterAsync("presenting", PresenterScene("s1", 1));
        var before = JsonSerializer.Serialize(await StoredAsync(project.Id));

        var picture = await Should.ThrowAsync<OperatorValidationException>(() => _operator.RegenerateScenePictureAsync(project.Id, "s1", None));
        var scene = await Should.ThrowAsync<OperatorValidationException>(() => _operator.RegenerateSceneAsync(project.Id, "s1", None));
        var narration = await Should.ThrowAsync<OperatorValidationException>(() => _operator.RegenerateNarrationAsync(project.Id, "s1", None));
        var afterRefusals = JsonSerializer.Serialize(await StoredAsync(project.Id));
        var text = await Should.ThrowAsync<OperatorValidationException>(() => _operator.EditSceneTextAsync(project.Id, "s1", "A new line.", None));
        var locked = await _operator.SetSceneLockedAsync(project.Id, "s1", true, None);

        picture.Message.ShouldBe(MakingPresenter);
        scene.Message.ShouldBe(MakingPresenter);
        narration.Message.ShouldBe(MakingPresenter);
        text.Message.ShouldBe(MakingPresenter);
        (locked.Status, locked.Scenes[0].Locked).ShouldBe(("presenting", true));
        (await StoredAsync(project.Id)).Status.ShouldBe("presenting");
        afterRefusals.ShouldBe(before);
    }

    [Fact]
    public async Task The_First_Presenter_Job_On_An_Owners_Picture_Saves_The_Version_Named_By_That_Picture()
    {
        var presenting = await PresentingAsync();
        await PutInMediaAsync(presenting.Id, PresenterName);
        var box = new MouthBox(0.4, 0.6, 0.2, 0.1);
        await SetStoredAsync(presenting.Id, p => p with { PresenterFile = PresenterName, PresenterSource = "owner" });

        var saved = await _operator.SaveProjectFromWorkerAsync(
            presenting with { PresenterRequest = string.Empty, PresenterVersions = [MadeVersion(PresenterName, null, box, "owner")] }, null, None);

        (saved.PresenterFile, saved.MouthBox).ShouldBe((PresenterName, (MouthBox?)box));
        var stored = await StoredAsync(presenting.Id);
        (stored.Status, stored.PresenterFile, stored.MouthBox).ShouldBe(("script_ready", (string?)PresenterName, (MouthBox?)box));
        var version = stored.PresenterVersions.ShouldHaveSingleItem();
        (version.File, version.Source).ShouldBe((PresenterName, "owner"));

        var marked = await PresentingAsync();
        await PutInMediaAsync(marked.Id, PresenterName);
        var ownMark = new MouthBox(0.1, 0.2, 0.3, 0.2);
        await SetStoredAsync(marked.Id, p => p with { PresenterFile = PresenterName, PresenterSource = "owner", MouthBox = ownMark });

        var keptMark = await _operator.SaveProjectFromWorkerAsync(
            marked with { PresenterRequest = string.Empty, PresenterVersions = [MadeVersion(PresenterName, null, box, "owner")] }, null, None);

        keptMark.MouthBox.ShouldBe(ownMark);
        (await StoredAsync(marked.Id)).MouthBox.ShouldBe(ownMark);
    }

    [Theory]
    [InlineData("first")]
    [InlineData("another")]
    [InlineData("prompt")]
    [InlineData("  prompt ")]
    public async Task Presenter_Feedback_That_Is_One_Of_The_Request_Words_Is_Refused(string feedback)
    {
        var project = await CharacterAsync("script_ready");
        var before = JsonSerializer.Serialize(await _store.ReadProjectsAsync(None));
        var logged = await _writes.ListAsync(500, None);

        var refused = await Should.ThrowAsync<OperatorValidationException>(() =>
            _operator.RequestPresenterAsync(project.Id, feedback, string.Empty, None));

        refused.Message.ShouldBe("Presenter feedback cannot be first, another or prompt.");
        JsonSerializer.Serialize(await _store.ReadProjectsAsync(None)).ShouldBe(before);
        (await _writes.ListAsync(500, None)).ShouldBe(logged);
    }

    [Fact]
    public async Task Using_An_Older_Version_Makes_It_Current()
    {
        var older = MadeVersion("presenter-1a2b.png", "presenter-preview-1.mp4", new MouthBox(0.4, 0.6, 0.2, 0.1));
        var newer = MadeVersion(Fox, null, new MouthBox(0.3, 0.5, 0.2, 0.1), "builtin", "builtin:fox");
        var project = await WithVersionsAsync("approved", older, newer);
        await RevokeYouTubeAsync();
        var logged = await _writes.ListAsync(500, None);
        var before = JsonSerializer.Serialize(await StoredAsync(project.Id));

        var missing = await Should.ThrowAsync<OperatorValidationException>(() => _operator.UsePresenterVersionAsync(project.Id, 2, None));
        var negative = await Should.ThrowAsync<OperatorValidationException>(() => _operator.UsePresenterVersionAsync(project.Id, -1, None));
        var afterRefusals = JsonSerializer.Serialize(await StoredAsync(project.Id));
        var used = await _operator.UsePresenterVersionAsync(project.Id, 0, None);

        missing.Message.ShouldBe("The project has no presenter version 2.");
        negative.Message.ShouldBe("The project has no presenter version -1.");
        afterRefusals.ShouldBe(before);
        (used.Status, used.PresenterFile, used.MouthBox, used.PresenterPreviewFile, used.PresenterSource)
            .ShouldBe(("media_ready", (string?)"presenter-1a2b.png", (MouthBox?)new MouthBox(0.4, 0.6, 0.2, 0.1), (string?)"presenter-preview-1.mp4", "generated"));
        var stored = await StoredAsync(project.Id);
        (stored.PresenterFile, stored.PresenterVersions.Count, stored.PresenterRequest).ShouldBe(("presenter-1a2b.png", 2, string.Empty));
        var package = stored.Package.ShouldNotBeNull();
        (package.Status, package.RightsConfirmed, package.ApprovedAt).ShouldBe(("assembled", false, (DateTimeOffset?)null));
        (await _writes.ListAsync(500, None)).ShouldBe(logged);
        var again = await _operator.UsePresenterVersionAsync(project.Id, 0, None);
        JsonSerializer.Serialize(again).ShouldBe(JsonSerializer.Serialize(used));
        JsonSerializer.Serialize(await StoredAsync(project.Id)).ShouldBe(JsonSerializer.Serialize(stored));
    }

    [Fact]
    public async Task The_Eleventh_Version_Prunes_The_Oldest_Unreferenced_One_And_Its_Files()
    {
        PresenterVersion[] ten =
        [
            MadeVersion(Fox, "presenter-preview-0.mp4", source: "builtin", feedback: "builtin:fox"),
            .. Enumerable.Range(1, 9).Select(i => MadeVersion($"presenter-{i:x4}.png", $"presenter-preview-{i}.mp4")),
        ];
        var project = await WithVersionsAsync("script_ready", ten);
        var media = await _operator.GetMediaDirectoryAsync(project.Id, None);
        await PutInMediaAsync(project.Id, "presenter-preview-10.mp4");
        await _operator.RequestPresenterAsync(project.Id, "builtin:fox", string.Empty, None);
        var presenting = (await _operator.NextQueuedProjectAsync(None)).ShouldNotBeNull();

        var saved = await _operator.SaveProjectFromWorkerAsync(
            presenting with
            {
                PresenterRequest = string.Empty,
                PresenterVersions = [.. presenting.PresenterVersions, MadeVersion(Fox, "presenter-preview-10.mp4", source: "builtin", feedback: "builtin:fox")],
            },
            null,
            None);

        saved.PresenterVersions.Select(v => v.PreviewFile).ShouldBe([
            "presenter-preview-0.mp4", .. Enumerable.Range(2, 9).Select(i => $"presenter-preview-{i}.mp4"),
        ]);
        (saved.PresenterFile, saved.PresenterPreviewFile).ShouldBe(((string?)Fox, (string?)"presenter-preview-10.mp4"));
        (await StoredAsync(project.Id)).PresenterVersions.Count.ShouldBe(10);
        File.Exists(Path.Combine(media, "presenter-0001.png")).ShouldBeFalse();
        File.Exists(Path.Combine(media, "presenter-preview-1.mp4")).ShouldBeFalse();
        File.Exists(Path.Combine(media, Fox)).ShouldBeTrue();
        File.Exists(Path.Combine(media, "presenter-preview-0.mp4")).ShouldBeTrue();
        File.Exists(Path.Combine(media, "presenter-0002.png")).ShouldBeTrue();
    }

    [Fact]
    public async Task A_Pruned_Version_Whose_Picture_Cannot_Be_Deleted_Still_Leaves_The_Store_Pruned()
    {
        var project = await WithVersionsAsync("script_ready", [.. Enumerable.Range(0, 10).Select(i => MadeVersion($"presenter-{i:x4}.png", null))]);
        var media = await _operator.GetMediaDirectoryAsync(project.Id, None);
        File.Delete(Path.Combine(media, "presenter-0000.png"));
        Directory.CreateDirectory(Path.Combine(media, "presenter-0000.png"));
        await PutInMediaAsync(project.Id, "presenter-000a.png");
        await _operator.RequestPresenterAsync(project.Id, "Make the fox smile.", string.Empty, None);
        var presenting = (await _operator.NextQueuedProjectAsync(None)).ShouldNotBeNull();

        var saved = await _operator.SaveProjectFromWorkerAsync(
            presenting with { PresenterRequest = string.Empty, PresenterVersions = [.. presenting.PresenterVersions, MadeVersion("presenter-000a.png", null)] },
            null,
            None);

        saved.PresenterVersions.Select(v => v.File).ShouldBe([.. Enumerable.Range(1, 10).Select(i => $"presenter-{i:x4}.png")]);
        (await StoredAsync(project.Id)).PresenterFile.ShouldBe("presenter-000a.png");
        Directory.Exists(Path.Combine(media, "presenter-0000.png")).ShouldBeTrue();
    }

    [Fact]
    public async Task Eleven_Versions_Of_One_Built_In_Presenter_Drop_The_Oldest_And_Keep_Its_Picture()
    {
        var project = await WithVersionsAsync("script_ready", [.. Enumerable.Range(0, 10).Select(i => MadeVersion(Fox, $"presenter-preview-{i}.mp4", source: "builtin"))]);
        var media = await _operator.GetMediaDirectoryAsync(project.Id, None);
        await PutInMediaAsync(project.Id, "presenter-preview-10.mp4");
        await _operator.RequestPresenterAsync(project.Id, "builtin:fox", string.Empty, None);
        var presenting = (await _operator.NextQueuedProjectAsync(None)).ShouldNotBeNull();

        var saved = await _operator.SaveProjectFromWorkerAsync(
            presenting with { PresenterRequest = string.Empty, PresenterVersions = [.. presenting.PresenterVersions, MadeVersion(Fox, "presenter-preview-10.mp4", source: "builtin")] },
            null,
            None);

        saved.PresenterVersions.Select(v => v.PreviewFile).ShouldBe([.. Enumerable.Range(1, 10).Select(i => $"presenter-preview-{i}.mp4")]);
        File.Exists(Path.Combine(media, Fox)).ShouldBeTrue();
        File.Exists(Path.Combine(media, "presenter-preview-0.mp4")).ShouldBeFalse();
    }

    [Fact]
    public async Task A_Versions_Picture_Survives_An_Owners_Attach_And_A_Presenter_Request()
    {
        var attached = await WithVersionsAsync("script_ready", MadeVersion("presenter-1a2b.png", "presenter-preview-1.mp4", new MouthBox(0.4, 0.6, 0.2, 0.1)));
        var regenerated = await WithVersionsAsync("script_ready", MadeVersion("presenter-3c4d.png", "presenter-preview-2.mp4"));
        var attachedMedia = await _operator.GetMediaDirectoryAsync(attached.Id, None);
        var regeneratedMedia = await _operator.GetMediaDirectoryAsync(regenerated.Id, None);
        await PutInMediaAsync(attached.Id, PresenterName);

        var owners = await _operator.AttachSceneMediaAsync(attached.Id, string.Empty, "presenter", PresenterName, None);
        var forgotten = await _operator.RegeneratePresenterAsync(regenerated.Id, None);

        (owners.PresenterFile, owners.MouthBox, owners.PresenterPreviewFile, owners.PresenterSource).ShouldBe(((string?)PresenterName, (MouthBox?)null, (string?)null, "owner"));
        File.Exists(Path.Combine(attachedMedia, "presenter-1a2b.png")).ShouldBeTrue();
        (forgotten.PresenterFile, forgotten.PresenterPreviewFile, forgotten.PresenterSource, forgotten.PresenterVersions.Count, forgotten.PresenterRequest)
            .ShouldBe(((string?)"presenter-3c4d.png", (string?)"presenter-preview-2.mp4", "generated", 1, "another"));
        File.Exists(Path.Combine(regeneratedMedia, "presenter-3c4d.png")).ShouldBeTrue();
        (await _operator.UsePresenterVersionAsync(attached.Id, 0, None)).PresenterFile.ShouldBe("presenter-1a2b.png");
    }

    [Theory]
    [InlineData("generated_pictures")]
    [InlineData("animated_character")]
    public async Task A_Scene_Picture_Prompt_Is_Edited_And_A_Picture_Regenerated_Keeping_The_Narration(string format)
    {
        var project = await ScriptReadyInAsync(format);
        var stock = StockScene("s2", 2);
        await SetStoredAsync(project.Id, p => p with
        {
            Status = "approved",
            Scenes = [Scene("s1", 1), stock, Scene("s3", 3, locked: true)],
            Package = ApprovedPackage(),
        });
        await RevokeYouTubeAsync();
        var logged = await _writes.ListAsync(500, None);

        var edited = await _operator.EditScenePicturePromptAsync(project.Id, "s1", "  A river at dawn,\tseen from above.  ", None);
        var tooLong = await Should.ThrowAsync<OperatorValidationException>(() => _operator.EditScenePicturePromptAsync(project.Id, "s1", new string('a', 701), None));
        var empty = await Should.ThrowAsync<OperatorValidationException>(() => _operator.EditScenePicturePromptAsync(project.Id, "s1", " \t ", None));
        var lockedPrompt = await Should.ThrowAsync<OperatorValidationException>(() => _operator.EditScenePicturePromptAsync(project.Id, "s3", "A loop.", None));
        var lockedPicture = await Should.ThrowAsync<OperatorValidationException>(() => _operator.RegenerateScenePictureAsync(project.Id, "s3", None));
        var afterEdit = await StoredAsync(project.Id);
        var regenerated = await _operator.RegenerateScenePictureAsync(project.Id, "s2", None);

        (edited.Status, edited.Scenes[0]).ShouldBe(("approved", Scene("s1", 1) with { VisualPrompt = "A river at dawn, seen from above." }));
        (tooLong.Message, empty.Message).ShouldBe(("A picture prompt is 1 to 700 characters.", "A picture prompt is 1 to 700 characters."));
        lockedPrompt.Message.ShouldBe("Scene 's3' is locked; unlock it before editing its picture prompt.");
        lockedPicture.Message.ShouldBe("Scene 's3' is locked; unlock it before regenerating it.");
        afterEdit.Scenes[0].VisualPrompt.ShouldBe("A river at dawn, seen from above.");
        (afterEdit.Status, afterEdit.Package.ShouldNotBeNull().RightsConfirmed).ShouldBe(("approved", true));
        regenerated.Status.ShouldBe("production_queued");
        regenerated.Scenes[1].ShouldBe(stock with { Status = "planned", VisualSource = "generated", VisualFile = null, VisualCredit = null });
        var package = (await StoredAsync(project.Id)).Package.ShouldNotBeNull();
        (package.Status, package.RightsConfirmed, package.ApprovedAt).ShouldBe(("assembled", false, (DateTimeOffset?)null));
        (await _writes.ListAsync(500, None)).ShouldBe(logged);
    }

    [Theory]
    [InlineData("own_video")]
    [InlineData("own_pictures")]
    public async Task Picture_Edits_Are_Refused_On_Own_Video_And_Own_Pictures(string format)
    {
        await GrantYouTubeAsync();
        var project = await _operator.StartProjectAsync("Why rivers meander", format, string.Empty, None);
        await SetStoredAsync(project.Id, p => p with { Status = "media_ready", Scenes = [Scene("s1", 1)] });
        var before = JsonSerializer.Serialize(await StoredAsync(project.Id));

        var prompt = await Should.ThrowAsync<OperatorValidationException>(() => _operator.EditScenePicturePromptAsync(project.Id, "s1", "A river.", None));
        var picture = await Should.ThrowAsync<OperatorValidationException>(() => _operator.RegenerateScenePictureAsync(project.Id, "s1", None));

        (prompt.Message, picture.Message).ShouldBe((NoGeneratedPicture, NoGeneratedPicture));
        JsonSerializer.Serialize(await StoredAsync(project.Id)).ShouldBe(before);
    }

    [Fact]
    public async Task An_Animated_Character_Scene_Takes_An_Attached_Image_And_A_Regenerate_Scene_Now()
    {
        var project = await CharacterAsync("media_ready", PresenterScene("s1", 1), PresenterScene("s2", 2));
        await PutInMediaAsync(project.Id, "mine.png");
        await SetStoredAsync(project.Id, p => p with { PresenterFile = PresenterName });

        var attached = await _operator.AttachSceneMediaAsync(project.Id, "s1", "visual", "mine.png", None);
        var regenerated = await _operator.RegenerateSceneAsync(project.Id, "s2", None);

        attached.Scenes[0].ShouldBe(PresenterScene("s1", 1) with { VisualSource = "owner", VisualFile = "mine.png" });
        (attached.Status, attached.PresenterFile).ShouldBe(("media_ready", (string?)PresenterName));
        regenerated.Status.ShouldBe("production_queued");
        regenerated.Scenes[1].ShouldBe(PresenterScene("s2", 2) with { Status = "planned", AudioFile = null, DurationMs = 0 });
        (await StoredAsync(project.Id)).PresenterFile.ShouldBe(PresenterName);
    }

    [Fact]
    public async Task The_Presenter_And_Picture_Edits_From_The_Chat_Are_Logged_Via_Chat_Under_The_Grant()
    {
        var character = await CharacterAsync("media_ready", PresenterScene("s1", 1));
        var before = (await _writes.ListAsync(500, None)).Count;

        var asked = await _operator.RequestPresenterFromChatAsync(character.Id, "Make the fox smile.", string.Empty, None);
        var prompt = await _operator.EditScenePicturePromptFromChatAsync(character.Id, "s1", "A river at dawn.", None);
        var picture = await _operator.RegenerateScenePictureFromChatAsync(character.Id, "s1", None);
        await RevokeYouTubeAsync();
        var refused = await Should.ThrowAsync<OperatorWriteRefusedException>(() =>
            _operator.RequestPresenterFromChatAsync(character.Id, "Make the fox frown.", string.Empty, None));
        var logged = await _writes.ListAsync(500, None);

        asked.PresenterRequest.ShouldBe("Make the fox smile.");
        prompt.Scenes[0].VisualPrompt.ShouldBe("A river at dawn.");
        (picture.Status, picture.Scenes[0].Status, picture.Scenes[0].VisualSource).ShouldBe(("production_queued", "planned", "generated"));
        refused.Message.ShouldBe(NotAllowed);
        (await StoredAsync(character.Id)).PresenterRequest.ShouldBe("Make the fox smile.");
        logged.Count.ShouldBe(before + 3);
        logged.Take(3).Select(e => (e.Target, e.Outcome)).ShouldBe([
            (character.Id, "asked for the picture of scene s1 again via chat"),
            (character.Id, "edited the picture prompt of scene s1 via chat"),
            (character.Id, "asked for a new presenter via chat"),
        ]);
    }
}
