using Scout.ControlPlane.Operator;
using Shouldly;
using Xunit;

namespace Scout.ControlPlane.Tests.Operator;

/// <summary>The owner's visual brief and motion preset, chosen when a project is produced or delegated.</summary>
public sealed partial class OperatorServiceTests
{
    private const string Watercolour = "Hand-drawn watercolour, soft pastel light.\nSlow and calm.";

    [Fact]
    public async Task Produce_And_Delegate_Store_The_Brief_And_The_Preset()
    {
        var produced = await ScriptReadyAsync();
        var delegated = await ScriptReadyAsync();
        var defaulted = await ScriptReadyAsync();

        var afterProduce = await _operator.ProduceProjectAsync(produced.Id, string.Empty, "  Hand-drawn\twatercolour, soft pastel light.\r\nSlow and calm.  ", " still ", None);
        var afterDelegate = await _operator.DelegateProjectAsync(delegated.Id, string.Empty, Watercolour, "dynamic", None);
        var afterDefault = await _operator.ProduceProjectAsync(defaulted.Id, string.Empty, "   ", "  ", None);

        (afterProduce.VisualBrief, afterProduce.Motion).ShouldBe(("Hand-drawn watercolour, soft pastel light. \nSlow and calm.", "still"));
        (afterDelegate.VisualBrief, afterDelegate.Motion).ShouldBe((Watercolour, "dynamic"));
        (afterDefault.VisualBrief, afterDefault.Motion).ShouldBe((string.Empty, "gentle"));
        var stored = await StoredAsync(produced.Id);
        (stored.VisualBrief, stored.Motion).ShouldBe(("Hand-drawn watercolour, soft pastel light. \nSlow and calm.", "still"));
        var storedDelegated = await StoredAsync(delegated.Id);
        (storedDelegated.VisualBrief, storedDelegated.Motion, storedDelegated.Delegated).ShouldBe((Watercolour, "dynamic", true));
    }

    [Fact]
    public async Task A_Brief_Over_500_Characters_Or_An_Unknown_Preset_Is_Refused()
    {
        var project = await ScriptReadyAsync();
        var atLimit = await ScriptReadyAsync();
        var overLimit = new string('a', 501);

        var longProduce = await Should.ThrowAsync<OperatorValidationException>(() =>
            _operator.ProduceProjectAsync(project.Id, string.Empty, overLimit, "gentle", None));
        var longDelegate = await Should.ThrowAsync<OperatorValidationException>(() =>
            _operator.DelegateProjectAsync(project.Id, string.Empty, overLimit, "gentle", None));
        var unknownProduce = await Should.ThrowAsync<OperatorValidationException>(() =>
            _operator.ProduceProjectAsync(project.Id, string.Empty, Watercolour, "Gentle", None));
        var unknownDelegate = await Should.ThrowAsync<OperatorValidationException>(() =>
            _operator.DelegateProjectAsync(project.Id, string.Empty, Watercolour, "zoompan=z=2", None));
        var accepted = await _operator.ProduceProjectAsync(atLimit.Id, string.Empty, " " + new string('a', 500) + " ", "gentle", None);

        longProduce.Message.ShouldBe("A visual brief is at most 500 characters.");
        longDelegate.Message.ShouldBe("A visual brief is at most 500 characters.");
        unknownProduce.Message.ShouldBe("Motion is still, gentle or dynamic.");
        unknownDelegate.Message.ShouldBe("Motion is still, gentle or dynamic.");
        accepted.VisualBrief.Length.ShouldBe(500);
        var stored = await StoredAsync(project.Id);
        (stored.Status, stored.Delegated, stored.VisualBrief, stored.Motion).ShouldBe(("script_ready", false, string.Empty, "gentle"));
    }

    [Fact]
    public async Task WorkerSave_And_Regenerate_Keep_The_Brief_And_The_Preset()
    {
        var project = await ScriptReadyAsync();
        await _operator.ProduceProjectAsync(project.Id, string.Empty, Watercolour, "dynamic", None);
        var taken = (await _operator.NextQueuedProjectAsync(None)).ShouldNotBeNull();

        var saved = await _operator.SaveProjectFromWorkerAsync(
            taken with { Status = "media_ready", Scenes = [Scene("s1", 1)], VisualBrief = "Neon glitch, fast cuts.", Motion = "still" }, null, None);
        var created = await _operator.SaveProjectFromWorkerAsync(
            NewDraft("Glaciers explained", "researching") with { VisualBrief = "Neon glitch, fast cuts.", Motion = "still" }, "discord:1", None);
        var regenerated = await _operator.RegenerateSceneAsync(project.Id, "s1", None);

        (saved.VisualBrief, saved.Motion).ShouldBe((Watercolour, "dynamic"));
        (regenerated.Status, regenerated.VisualBrief, regenerated.Motion).ShouldBe(("production_queued", Watercolour, "dynamic"));
        var stored = await StoredAsync(project.Id);
        (stored.VisualBrief, stored.Motion).ShouldBe((Watercolour, "dynamic"));
        var storedCreated = await StoredAsync(created.Id);
        (storedCreated.VisualBrief, storedCreated.Motion).ShouldBe((string.Empty, "gentle"));
    }
}
