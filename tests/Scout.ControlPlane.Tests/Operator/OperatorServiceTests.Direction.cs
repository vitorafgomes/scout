using Scout.ControlPlane.Operator;
using Shouldly;
using Xunit;

namespace Scout.ControlPlane.Tests.Operator;

/// <summary>The owner's direction and motion preset, given when a project is started, produced or delegated.</summary>
public sealed partial class OperatorServiceTests
{
    private const string Watercolour = "Hand-drawn watercolour, soft pastel light.\nSlow and calm.";

    [Fact]
    public async Task Produce_And_Delegate_Store_The_Direction_And_The_Preset()
    {
        var produced = await ScriptReadyAsync();
        var delegated = await ScriptReadyAsync();
        var defaulted = await ScriptReadyAsync();

        var afterProduce = await _operator.ProduceProjectAsync(produced.Id, string.Empty, "  Hand-drawn\twatercolour, soft pastel light.\r\nSlow and calm.  ", " still ", None);
        var afterDelegate = await _operator.DelegateProjectAsync(delegated.Id, string.Empty, Watercolour, "dynamic", None);
        var afterDefault = await _operator.ProduceProjectAsync(defaulted.Id, string.Empty, "   ", "  ", None);

        (afterProduce.Direction, afterProduce.Motion).ShouldBe(("Hand-drawn watercolour, soft pastel light. \nSlow and calm.", "still"));
        (afterDelegate.Direction, afterDelegate.Motion).ShouldBe((Watercolour, "dynamic"));
        (afterDefault.Direction, afterDefault.Motion).ShouldBe((string.Empty, "gentle"));
        var stored = await StoredAsync(produced.Id);
        (stored.Direction, stored.Motion).ShouldBe(("Hand-drawn watercolour, soft pastel light. \nSlow and calm.", "still"));
        var storedDelegated = await StoredAsync(delegated.Id);
        (storedDelegated.Direction, storedDelegated.Motion, storedDelegated.Delegated).ShouldBe((Watercolour, "dynamic", true));
    }

    [Fact]
    public async Task A_Direction_Over_1000_Characters_Is_Refused_At_Start_Produce_And_Delegate()
    {
        var project = await ScriptReadyAsync();
        var atLimit = await ScriptReadyAsync();
        var overLimit = new string('a', 1001);
        var before = (await _store.ReadProjectsAsync(None)).Count;

        var longStart = await Should.ThrowAsync<OperatorValidationException>(() =>
            _operator.StartProjectAsync("Glaciers explained", string.Empty, overLimit, None));
        var longProduce = await Should.ThrowAsync<OperatorValidationException>(() =>
            _operator.ProduceProjectAsync(project.Id, string.Empty, overLimit, "gentle", None));
        var longDelegate = await Should.ThrowAsync<OperatorValidationException>(() =>
            _operator.DelegateProjectAsync(project.Id, string.Empty, overLimit, "gentle", None));
        var afterRefusals = (await _store.ReadProjectsAsync(None)).Count;
        var accepted = await _operator.ProduceProjectAsync(atLimit.Id, string.Empty, " " + new string('a', 1000) + " ", "gentle", None);
        var started = await _operator.StartProjectAsync("Glaciers explained", string.Empty, new string('b', 1000), None);

        longStart.Message.ShouldBe("A direction is at most 1000 characters.");
        longProduce.Message.ShouldBe("A direction is at most 1000 characters.");
        longDelegate.Message.ShouldBe("A direction is at most 1000 characters.");
        afterRefusals.ShouldBe(before);
        accepted.Direction.ShouldBe(new string('a', 1000));
        (await StoredAsync(started.Id)).Direction.ShouldBe(new string('b', 1000));
        var stored = await StoredAsync(project.Id);
        (stored.Status, stored.Delegated, stored.Direction, stored.Motion).ShouldBe(("script_ready", false, string.Empty, "gentle"));
    }

    [Fact]
    public async Task An_Unknown_Motion_Preset_Is_Refused_At_Produce_And_Delegate()
    {
        var project = await ScriptReadyAsync();

        var unknownProduce = await Should.ThrowAsync<OperatorValidationException>(() =>
            _operator.ProduceProjectAsync(project.Id, string.Empty, Watercolour, "Gentle", None));
        var unknownDelegate = await Should.ThrowAsync<OperatorValidationException>(() =>
            _operator.DelegateProjectAsync(project.Id, string.Empty, Watercolour, "zoompan=z=2", None));

        unknownProduce.Message.ShouldBe("Motion is still, gentle or dynamic.");
        unknownDelegate.Message.ShouldBe("Motion is still, gentle or dynamic.");
        var stored = await StoredAsync(project.Id);
        (stored.Status, stored.Delegated, stored.Direction, stored.Motion).ShouldBe(("script_ready", false, string.Empty, "gentle"));
    }

    [Fact]
    public async Task WorkerSave_And_Regenerate_Keep_The_Direction_And_The_Preset()
    {
        var project = await ScriptReadyAsync();
        await _operator.ProduceProjectAsync(project.Id, string.Empty, Watercolour, "dynamic", None);
        var taken = (await _operator.NextQueuedProjectAsync(None)).ShouldNotBeNull();

        var saved = await _operator.SaveProjectFromWorkerAsync(
            taken with { Status = "media_ready", Scenes = [Scene("s1", 1)], Direction = "Neon glitch, fast cuts.", Motion = "still" }, null, None);
        var created = await _operator.SaveProjectFromWorkerAsync(
            NewDraft("Glaciers explained", "researching") with { Direction = "Neon glitch, fast cuts.", Motion = "still" }, "discord:1", None);
        var regenerated = await _operator.RegenerateSceneAsync(project.Id, "s1", None);

        (saved.Direction, saved.Motion).ShouldBe((Watercolour, "dynamic"));
        (regenerated.Status, regenerated.Direction, regenerated.Motion).ShouldBe(("production_queued", Watercolour, "dynamic"));
        var stored = await StoredAsync(project.Id);
        (stored.Direction, stored.Motion).ShouldBe((Watercolour, "dynamic"));
        var storedCreated = await StoredAsync(created.Id);
        (storedCreated.Direction, storedCreated.Motion).ShouldBe(("Neon glitch, fast cuts.", "gentle"));
    }
}
