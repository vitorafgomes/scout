using Scout.ControlPlane.Operator;
using Shouldly;
using Xunit;

namespace Scout.ControlPlane.Tests.Operator;

/// <summary>The owner's scene text and the narration made again, in every format, without a grant.</summary>
public sealed partial class OperatorServiceTests
{
    private static readonly Scene OwnersScene =
        Scene("s1", 1, audioSource: "owner", visualSource: "owner", audioFile: "mine.mp3", visualFile: "mine.png") with { Error = "A note." };

    private static readonly Scene GeneratedScene = Scene("s2", 2, audioFile: "scene-s2.mp3", visualFile: "scene-s2.png");

    private static readonly Scene StockPictureScene = StockScene("s3", 3, file: "scene-s3.jpg");

    private async Task<OperatorProject> WithStoredScenesAsync(string status, params Scene[] scenes)
    {
        var project = await ScriptReadyAsync();
        await SetStoredAsync(project.Id, p => p with { Status = status, Scenes = scenes });
        return await StoredAsync(project.Id);
    }

    private static (string Status, string? Error, string AudioSource, string? AudioFile, int DurationMs) NarrationOf(Scene scene) =>
        (scene.Status, scene.Error, scene.AudioSource, scene.AudioFile, scene.DurationMs);

    private static (string VisualSource, string? VisualFile, string? VisualCredit, string VisualPrompt, string VisualQuery) PictureOf(Scene scene) =>
        (scene.VisualSource, scene.VisualFile, scene.VisualCredit, scene.VisualPrompt, scene.VisualQuery);

    [Fact]
    public async Task EditSceneText_Sets_The_Cleaned_Text_And_Refuses_Empty_Or_Over_700_Characters()
    {
        var project = await WithStoredScenesAsync("media_ready", GeneratedScene, Scene("s4", 4));

        var edited = await _operator.EditSceneTextAsync(project.Id, "s2", "  Rivers\tbend.\r\nThen they loop.  ", None);
        var empty = await Should.ThrowAsync<OperatorValidationException>(() => _operator.EditSceneTextAsync(project.Id, "s2", " \t\r\n ", None));
        var tooLong = await Should.ThrowAsync<OperatorValidationException>(() => _operator.EditSceneTextAsync(project.Id, "s2", new string('a', 701), None));
        var afterRefusals = await StoredAsync(project.Id);
        var atLimit = await _operator.EditSceneTextAsync(project.Id, "s4", " " + new string('b', 700) + " ", None);

        edited.Scenes.Single(s => s.Id == "s2").Narration.ShouldBe("Rivers bend. \nThen they loop.");
        empty.Message.ShouldBe("A scene's text is 1 to 700 characters.");
        tooLong.Message.ShouldBe("A scene's text is 1 to 700 characters.");
        afterRefusals.Scenes.Select(s => (s.Id, s.Narration)).ShouldBe([("s2", "Rivers bend. \nThen they loop."), ("s4", "Narration s4")]);
        atLimit.Scenes.Single(s => s.Id == "s4").Narration.ShouldBe(new string('b', 700));
        (await StoredAsync(project.Id)).Scenes.Single(s => s.Id == "s4").Narration.ShouldBe(new string('b', 700));
    }

    [Fact]
    public async Task EditSceneText_Resets_The_Narration_Owners_Audio_Included_And_Keeps_The_Picture()
    {
        var project = await WithStoredScenesAsync("media_ready", OwnersScene, GeneratedScene, StockPictureScene);

        foreach (var id in new[] { "s1", "s2", "s3" })
        {
            await _operator.EditSceneTextAsync(project.Id, id, $"New text of {id}.", None);
        }

        var stored = await StoredAsync(project.Id);
        stored.Scenes.Select(s => s.Narration).ShouldBe(["New text of s1.", "New text of s2.", "New text of s3."]);
        stored.Scenes.Select(NarrationOf).ShouldBe(Enumerable.Repeat(("planned", (string?)null, "none", (string?)null, 0), 3));
        stored.Scenes.Select(PictureOf).ShouldBe([PictureOf(OwnersScene), PictureOf(GeneratedScene), PictureOf(StockPictureScene)]);
        stored.Scenes.Select(s => (s.Order, s.Locked)).ShouldBe([(1, false), (2, false), (3, false)]);
    }

    [Theory]
    [InlineData("media_ready", null, null)]
    [InlineData("producing", null, null)]
    [InlineData("assembly_queued", "assembly_queued", "assembly_queued")]
    [InlineData("assembling", "assembling", "assembly_queued")]
    [InlineData("assembled", "assembled", "assembled")]
    [InlineData("approved", "approved", "assembled")]
    [InlineData("failed", null, null)]
    public async Task EditSceneText_Queues_Production_From_Every_Status_With_Scenes(string status, string? packageBefore, string? packageAfter)
    {
        var project = await WithStoredScenesAsync(status, GeneratedScene);
        if (packageBefore is not null)
        {
            await SetStoredAsync(project.Id, p => p with
            {
                Package = new VideoPackage(packageBefore, "video.mp4", [], null, [], true, DateTimeOffset.UnixEpoch, "Title", "Description", [], 6000, null, false),
            });
        }

        var edited = await _operator.EditSceneTextAsync(project.Id, "s2", "Rivers bend.", None);

        edited.Status.ShouldBe("production_queued");
        var stored = await StoredAsync(project.Id);
        stored.Status.ShouldBe("production_queued");
        if (packageAfter is null)
        {
            stored.Package.ShouldBeNull();
        }
        else
        {
            var package = stored.Package.ShouldNotBeNull();
            package.Status.ShouldBe(packageAfter);
            if (packageAfter == "assembled")
            {
                (package.RightsConfirmed, package.ApprovedAt).ShouldBe((false, (DateTimeOffset?)null));
            }
        }
    }

    [Fact]
    public async Task EditSceneText_And_RegenerateNarration_Refuse_A_Locked_Scene()
    {
        var project = await WithStoredScenesAsync("media_ready", GeneratedScene with { Locked = true });
        var before = await StoredAsync(project.Id);

        var edit = await Should.ThrowAsync<OperatorValidationException>(() => _operator.EditSceneTextAsync(project.Id, "s2", "Rivers bend.", None));
        var regenerate = await Should.ThrowAsync<OperatorValidationException>(() => _operator.RegenerateNarrationAsync(project.Id, "s2", None));

        edit.Message.ShouldBe("Scene 's2' is locked; unlock it before editing its text.");
        regenerate.Message.ShouldBe("Scene 's2' is locked; unlock it before regenerating it.");
        var after = await StoredAsync(project.Id);
        (after.Status, after.Scenes.ShouldHaveSingleItem()).ShouldBe((before.Status, before.Scenes.Single()));
    }

    [Fact]
    public async Task EditSceneText_And_RegenerateNarration_Answer_Not_Found_For_An_Unknown_Scene()
    {
        var project = await WithStoredScenesAsync("media_ready", GeneratedScene);

        var edit = await Should.ThrowAsync<OperatorNotFoundException>(() => _operator.EditSceneTextAsync(project.Id, "s9", "Rivers bend.", None));
        var regenerate = await Should.ThrowAsync<OperatorNotFoundException>(() => _operator.RegenerateNarrationAsync(project.Id, "s9", None));

        edit.Message.ShouldBe($"Unknown scene 's9' in project '{project.Id}'.");
        regenerate.Message.ShouldBe($"Unknown scene 's9' in project '{project.Id}'.");
        (await StoredAsync(project.Id)).Status.ShouldBe("media_ready");
    }

    [Fact]
    public async Task EditSceneText_And_RegenerateNarration_Need_No_Grant_And_Write_No_Log_Entry()
    {
        var project = await WithStoredScenesAsync("media_ready", GeneratedScene, StockPictureScene);
        await RevokeYouTubeAsync();
        var logged = await _writes.ListAsync(50, None);

        var edited = await _operator.EditSceneTextAsync(project.Id, "s2", "Rivers bend.", None);
        var regenerated = await _operator.RegenerateNarrationAsync(project.Id, "s3", None);

        (edited.Status, regenerated.Status).ShouldBe(("production_queued", "production_queued"));
        var stored = await StoredAsync(project.Id);
        stored.Scenes.Select(s => (s.Narration, s.Status, s.AudioSource)).ShouldBe([("Rivers bend.", "planned", "none"), ("Narration s3", "planned", "none")]);
        (await _writes.ListAsync(50, None)).ShouldBe(logged);
    }

    [Fact]
    public async Task RegenerateNarration_Resets_Only_The_Narration_And_Queues_Production()
    {
        var longText = string.Join(' ', Enumerable.Repeat("Rivers bend.", 70));
        var project = await WithStoredScenesAsync("assembled", OwnersScene with { Narration = longText }, StockPictureScene);

        var regenerated = await _operator.RegenerateNarrationAsync(project.Id, "s1", None);

        regenerated.Status.ShouldBe("production_queued");
        var stored = await StoredAsync(project.Id);
        var scene = stored.Scenes.First();
        longText.Length.ShouldBeGreaterThan(700);
        scene.Narration.ShouldBe(longText);
        NarrationOf(scene).ShouldBe(("planned", (string?)null, "none", (string?)null, 0));
        PictureOf(scene).ShouldBe(PictureOf(OwnersScene));
        stored.Scenes.Last().ShouldBe(StockPictureScene);
    }
}
