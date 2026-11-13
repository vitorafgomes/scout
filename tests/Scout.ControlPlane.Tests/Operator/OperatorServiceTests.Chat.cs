using System.Text.Json;
using Scout.ControlPlane.Actions;
using Scout.ControlPlane.Features;
using Scout.ControlPlane.Operator;
using Scout.ControlPlane.WriteGate;
using Shouldly;
using Xunit;

namespace Scout.ControlPlane.Tests.Operator;

/// <summary>The owner's edits from the chat: under the YouTube grant, logged as chat actions, Scout's own state only.</summary>
public sealed partial class OperatorServiceTests
{
    private const string CoriVoice = "piper:en_GB-cori-medium";

    private Task<OperatorProject> FromChatAsync(string edit, string projectId) => edit switch
    {
        "edit_text" => _operator.EditSceneTextFromChatAsync(projectId, "s2", "Rivers bend.", None),
        "regenerate_narration" => _operator.RegenerateNarrationFromChatAsync(projectId, "s2", None),
        "regenerate_scene" => _operator.RegenerateSceneFromChatAsync(projectId, "s2", None),
        "lock" => _operator.SetSceneLockedFromChatAsync(projectId, "s2", true, None),
        "unlock" => _operator.SetSceneLockedFromChatAsync(projectId, "s2", false, None),
        "reorder" => _operator.ReorderScenesFromChatAsync(projectId, ["s4", "s2"], None),
        "produce" => _operator.ProduceProjectFromChatAsync(projectId, string.Empty, string.Empty, string.Empty, None),
        "delegate" => _operator.DelegateProjectFromChatAsync(projectId, string.Empty, string.Empty, string.Empty, None),
        _ => throw new ArgumentOutOfRangeException(nameof(edit), edit, null),
    };

    private static string ScenesOf(OperatorProject project) =>
        $"{project.Status}|" + string.Join(",", project.Scenes.OrderBy(s => s.Order).Select(s => $"{s.Id}:{s.Status}:{s.Locked}:{s.AudioSource}"));

    private Dictionary<string, string> FilesOtherThanProjectsAndWriteLog() =>
        Directory.EnumerateFiles(_directory, "*", SearchOption.AllDirectories)
            .Where(f => Path.GetFileName(f) is not ("projects.json" or "writes.log"))
            .ToDictionary(f => Path.GetRelativePath(_directory, f), File.ReadAllText, StringComparer.Ordinal);

    private async Task NothingQueuedForTheOwnerAsync()
    {
        (await _actions.ListPendingAsync(None)).ShouldBeEmpty();
        (await _actions.ListOpenAsync(FeatureCatalog.YouTubeId, None)).ShouldBeEmpty();
    }

    private async Task<WriteLogEntry> OnlyNewEntryAsync(int before)
    {
        var entries = await _writes.ListAsync(500, None);
        entries.Count.ShouldBe(before + 1);
        return entries[0];
    }

    [Fact]
    public async Task A_Chat_Text_Edit_Changes_The_Scene_And_Logs_Edited_Via_Chat()
    {
        var project = await WithStoredScenesAsync("media_ready", GeneratedScene, Scene("s4", 4));
        var before = (await _writes.ListAsync(500, None)).Count;
        var otherFiles = FilesOtherThanProjectsAndWriteLog();

        var edited = await _operator.EditSceneTextFromChatAsync(project.Id, "s2", "  Rivers\tbend.  ", None);

        edited.Status.ShouldBe("production_queued");
        var scene = (await StoredAsync(project.Id)).Scenes.Single(s => s.Id == "s2");
        (scene.Narration, scene.Status, scene.AudioSource, scene.AudioFile).ShouldBe(("Rivers bend.", "planned", "none", (string?)null));
        var entry = await OnlyNewEntryAsync(before);
        (entry.FeatureId, entry.Action, entry.Target, entry.Outcome, entry.ApprovedBy, entry.ActionId)
            .ShouldBe(("youtube", "Scout's data folder", project.Id, "edited the text of scene s2 via chat", (string?)null, (string?)null));
        await NothingQueuedForTheOwnerAsync();
        FilesOtherThanProjectsAndWriteLog().ShouldBe(otherFiles);
    }

    [Theory]
    [InlineData("edit_text")]
    [InlineData("regenerate_narration")]
    [InlineData("regenerate_scene")]
    [InlineData("lock")]
    [InlineData("reorder")]
    [InlineData("produce")]
    [InlineData("delegate")]
    public async Task Every_Chat_Edit_Without_The_Grant_Is_Refused_Stores_Nothing_And_Logs_Nothing(string edit)
    {
        var project = await WithStoredScenesAsync("media_ready", GeneratedScene, Scene("s4", 4));
        await RevokeYouTubeAsync();
        var stored = JsonSerializer.Serialize(await StoredAsync(project.Id));
        var logged = await _writes.ListAsync(500, None);

        var refused = await Should.ThrowAsync<OperatorWriteRefusedException>(() => FromChatAsync(edit, project.Id));

        refused.Message.ShouldBe(NotAllowed);
        JsonSerializer.Serialize(await StoredAsync(project.Id)).ShouldBe(stored);
        (await _writes.ListAsync(500, None)).ShouldBe(logged);
        await NothingQueuedForTheOwnerAsync();
    }

    [Theory]
    [InlineData("regenerate_narration", false, "asked for the narration of scene s2 again via chat", "production_queued|s2:planned:False:none,s4:ready:False:generated")]
    [InlineData("regenerate_scene", false, "asked for scene s2 again via chat", "production_queued|s2:planned:False:generated,s4:ready:False:generated")]
    [InlineData("lock", false, "locked scene s2 via chat", "media_ready|s2:ready:True:generated,s4:ready:False:generated")]
    [InlineData("unlock", true, "unlocked scene s2 via chat", "media_ready|s2:ready:False:generated,s4:ready:False:generated")]
    [InlineData("reorder", false, "reordered the scenes via chat", "media_ready|s4:ready:False:generated,s2:ready:False:generated")]
    public async Task Chat_Regenerate_Lock_And_Reorder_Log_Their_Outcome_Via_Chat(string edit, bool lockedBefore, string outcome, string scenes)
    {
        var project = await WithStoredScenesAsync("media_ready", GeneratedScene with { Locked = lockedBefore }, Scene("s4", 4));
        var before = (await _writes.ListAsync(500, None)).Count;

        var changed = await FromChatAsync(edit, project.Id);

        ScenesOf(changed).ShouldBe(scenes);
        ScenesOf(await StoredAsync(project.Id)).ShouldBe(scenes);
        var entry = await OnlyNewEntryAsync(before);
        (entry.Action, entry.Target, entry.Outcome, entry.ApprovedBy).ShouldBe(("Scout's data folder", project.Id, outcome, (string?)null));
        await NothingQueuedForTheOwnerAsync();
    }

    [Fact]
    public async Task Chat_Produce_And_Delegate_Keep_The_Stored_Voice_Direction_And_Motion_When_Blank()
    {
        var produced = await WithStoredScenesAsync("media_ready", GeneratedScene);
        var delegated = await WithStoredScenesAsync("media_ready", GeneratedScene);
        var chosen = await WithStoredScenesAsync("media_ready", GeneratedScene);
        foreach (var id in new[] { produced.Id, delegated.Id, chosen.Id })
        {
            await SetStoredAsync(id, p => p with { NarrationVoice = CoriVoice, Direction = Watercolour, Motion = "dynamic" });
        }
        var before = (await _writes.ListAsync(500, None)).Count;

        var afterProduce = await _operator.ProduceProjectFromChatAsync(produced.Id, string.Empty, "  ", "\t", None);
        var afterDelegate = await _operator.DelegateProjectFromChatAsync(delegated.Id, " ", string.Empty, string.Empty, None);
        var afterChoice = await _operator.ProduceProjectFromChatAsync(chosen.Id, " OpenAI:nova ", "Calm and slow.", "still", None);

        (afterProduce.Status, afterProduce.NarrationVoice, afterProduce.Direction, afterProduce.Motion)
            .ShouldBe(("production_queued", CoriVoice, Watercolour, "dynamic"));
        (afterDelegate.Status, afterDelegate.Delegated, afterDelegate.NarrationVoice, afterDelegate.Direction, afterDelegate.Motion)
            .ShouldBe(("assembly_queued", true, CoriVoice, Watercolour, "dynamic"));
        (afterChoice.NarrationVoice, afterChoice.Direction, afterChoice.Motion).ShouldBe(("OpenAI:nova", "Calm and slow.", "still"));
        var storedProduce = await StoredAsync(produced.Id);
        (storedProduce.NarrationVoice, storedProduce.Direction, storedProduce.Motion).ShouldBe((CoriVoice, Watercolour, "dynamic"));
        var storedDelegate = await StoredAsync(delegated.Id);
        (storedDelegate.NarrationVoice, storedDelegate.Direction, storedDelegate.Motion).ShouldBe((CoriVoice, Watercolour, "dynamic"));
        (await _writes.ListAsync(500, None)).Take(3).Select(e => (e.Target, e.Outcome)).ShouldBe([
            (chosen.Id, "queued production via chat"),
            (delegated.Id, "delegated via chat"),
            (produced.Id, "queued production via chat"),
        ]);
        (await _writes.ListAsync(500, None)).Count.ShouldBe(before + 3);
        await NothingQueuedForTheOwnerAsync();
    }

    [Fact]
    public async Task A_Chat_Edit_Of_A_Locked_Scene_Is_Refused_And_Logs_Nothing()
    {
        var project = await WithStoredScenesAsync("media_ready", GeneratedScene with { Locked = true });
        var stored = JsonSerializer.Serialize(await StoredAsync(project.Id));
        var logged = await _writes.ListAsync(500, None);

        var edit = await Should.ThrowAsync<OperatorValidationException>(() => _operator.EditSceneTextFromChatAsync(project.Id, "s2", "Rivers bend.", None));
        var narration = await Should.ThrowAsync<OperatorValidationException>(() => _operator.RegenerateNarrationFromChatAsync(project.Id, "s2", None));
        var scene = await Should.ThrowAsync<OperatorValidationException>(() => _operator.RegenerateSceneFromChatAsync(project.Id, "s2", None));

        edit.Message.ShouldBe("Scene 's2' is locked; unlock it before editing its text.");
        narration.Message.ShouldBe("Scene 's2' is locked; unlock it before regenerating it.");
        scene.Message.ShouldBe("Scene 's2' is locked; unlock it before regenerating it.");
        JsonSerializer.Serialize(await StoredAsync(project.Id)).ShouldBe(stored);
        (await _writes.ListAsync(500, None)).ShouldBe(logged);
    }

    [Fact]
    public async Task A_Chat_Change_Stands_And_Answers_When_The_Write_Log_Cannot_Append()
    {
        var project = await WithStoredScenesAsync("media_ready", GeneratedScene, Scene("s4", 4));
        var unlogged = new OperatorService(_store, new WriteGateService(new FeatureCatalog(), _states, _actions, _writes), new UnwritableLog());
        var logged = await _writes.ListAsync(500, None);

        var locked = await unlogged.SetSceneLockedFromChatAsync(project.Id, "s2", true, None);

        ScenesOf(locked).ShouldBe("media_ready|s2:ready:True:generated,s4:ready:False:generated");
        ScenesOf(await StoredAsync(project.Id)).ShouldBe("media_ready|s2:ready:True:generated,s4:ready:False:generated");
        (await _writes.ListAsync(500, None)).ShouldBe(logged);
    }

    [Fact]
    public async Task A_Chat_Change_That_Changes_Nothing_Writes_No_Log_Entry_And_Still_Answers()
    {
        var project = await WithStoredScenesAsync("media_ready", GeneratedScene with { Order = 1, Locked = true }, Scene("s4", 2));
        var logged = await _writes.ListAsync(500, None);

        var reordered = await _operator.ReorderScenesFromChatAsync(project.Id, ["s2", "s4"], None);
        var locked = await _operator.SetSceneLockedFromChatAsync(project.Id, "s2", true, None);

        ScenesOf(reordered).ShouldBe("media_ready|s2:ready:True:generated,s4:ready:False:generated");
        ScenesOf(locked).ShouldBe("media_ready|s2:ready:True:generated,s4:ready:False:generated");
        (await _writes.ListAsync(500, None)).ShouldBe(logged);
    }

    [Fact]
    public async Task A_Chat_Regeneration_Of_An_Own_Video_Scene_Is_Refused_And_Logs_Nothing()
    {
        var project = await OwnVideoWithScenesAsync("media_ready", RecordingScene("s1", 1, 0, 4200), RecordingScene("s2", 2, 4200, 9000));
        var stored = JsonSerializer.Serialize(await StoredAsync(project.Id));
        var logged = await _writes.ListAsync(500, None);

        var scene = await Should.ThrowAsync<OperatorValidationException>(() => _operator.RegenerateSceneFromChatAsync(project.Id, "s2", None));
        var narration = await Should.ThrowAsync<OperatorValidationException>(() => _operator.RegenerateNarrationFromChatAsync(project.Id, "s2", None));

        scene.Message.ShouldBe(NothingToMakeAgain);
        narration.Message.ShouldBe(NothingToMakeAgain);
        JsonSerializer.Serialize(await StoredAsync(project.Id)).ShouldBe(stored);
        (await _writes.ListAsync(500, None)).ShouldBe(logged);
        await NothingQueuedForTheOwnerAsync();
    }

    private sealed class UnwritableLog : IWriteLog
    {
        public Task AppendAsync(WriteLogEntry entry, CancellationToken ct) => throw new IOException("The disk is full.");

        public Task<IReadOnlyList<WriteLogEntry>> ListAsync(int limit, CancellationToken ct) => Task.FromResult<IReadOnlyList<WriteLogEntry>>([]);

        public Task<IReadOnlyList<WriteLogEntry>> ListSinceAsync(DateTimeOffset since, CancellationToken ct) => Task.FromResult<IReadOnlyList<WriteLogEntry>>([]);
    }
}
