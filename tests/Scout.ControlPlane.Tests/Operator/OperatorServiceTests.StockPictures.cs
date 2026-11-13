using Scout.ControlPlane.Operator;
using Shouldly;
using Xunit;

namespace Scout.ControlPlane.Tests.Operator;

/// <summary>Stock pictures from Openverse: the worker's save check, the owner's attach over one and a regenerate.</summary>
public sealed partial class OperatorServiceTests
{
    private const string Credit = "\"Mountain lake\" by Thomas Nes Myhre is licensed under CC BY 2.0.";

    private static Scene StockScene(string id, int order, string? file = "scene-s1.jpg", string? credit = Credit) =>
        Scene(id, order, visualSource: "stock") with { VisualFile = file, VisualCredit = credit, VisualQuery = "mountain lake dawn" };

    [Fact]
    public async Task A_stock_visual_needs_a_file_and_a_credit()
    {
        var accepted = await ProducingWithScenesAsync(StockScene("s1", 1));
        var withoutCredit = await Should.ThrowAsync<OperatorValidationException>(() => ProducingWithScenesAsync(StockScene("s1", 1, credit: " ")));
        var withoutFile = await Should.ThrowAsync<OperatorValidationException>(() => ProducingWithScenesAsync(StockScene("s1", 1, file: null)));

        var stored = (await StoredAsync(accepted.Id)).Scenes.ShouldHaveSingleItem();
        (stored.VisualSource, stored.VisualFile, stored.VisualCredit, stored.VisualQuery)
            .ShouldBe(("stock", (string?)"scene-s1.jpg", (string?)Credit, "mountain lake dawn"));
        withoutCredit.Message.ShouldBe("Scene 's1' shows a stock picture; it needs the picture's file and its credit.");
        withoutFile.Message.ShouldBe("Scene 's1' shows a stock picture; it needs the picture's file and its credit.");
    }

    [Fact]
    public async Task An_owner_attach_replaces_a_stock_picture_and_clears_the_credit()
    {
        var project = await ProducingWithScenesAsync(StockScene("s1", 1), Scene("s2", 2, status: "planned"));
        var media = await _operator.GetMediaDirectoryAsync(project.Id, CancellationToken.None);
        await File.WriteAllTextAsync(Path.Combine(media, "my map.png"), "image");
        var read = await _operator.GetProjectForWorkerAsync(project.Id, CancellationToken.None);

        var attached = await _operator.AttachSceneMediaAsync(project.Id, "s1", "visual", "my map.png", CancellationToken.None);
        var saved = await _operator.SaveProjectFromWorkerAsync(
            read with { Scenes = [read.Scenes[0], read.Scenes[1] with { Status = "ready" }] }, null, CancellationToken.None);

        var owner = attached.Scenes.Single(s => s.Id == "s1");
        (owner.VisualSource, owner.VisualFile, owner.VisualCredit).ShouldBe(("owner", (string?)"my map.png", (string?)null));
        var kept = saved.Scenes.Single(s => s.Id == "s1");
        (kept.VisualSource, kept.VisualFile, kept.VisualCredit).ShouldBe(("owner", (string?)"my map.png", (string?)null));
    }

    [Fact]
    public async Task A_regenerated_stock_scene_becomes_a_text_card_without_its_credit_and_the_next_worker_save_is_accepted()
    {
        var project = await WithScenesAsync(StockScene("s1", 1), Scene("s2", 2));

        var regenerated = await _operator.RegenerateSceneAsync(project.Id, "s1", CancellationToken.None);
        var taken = (await _operator.NextQueuedProjectAsync(CancellationToken.None)).ShouldNotBeNull();
        var saved = await _operator.SaveProjectFromWorkerAsync(
            taken with { Scenes = [taken.Scenes[0], taken.Scenes[1] with { Error = "No narration." }] }, null, CancellationToken.None);

        var scene = regenerated.Scenes.Single(s => s.Id == "s1");
        (scene.Status, scene.VisualSource, scene.VisualFile, scene.VisualCredit)
            .ShouldBe(("planned", "text_card", (string?)null, (string?)null));
        saved.Scenes.Single(s => s.Id == "s2").Error.ShouldBe("No narration.");
    }
}
