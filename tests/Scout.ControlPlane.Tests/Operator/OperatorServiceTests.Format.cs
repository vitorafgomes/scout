using Scout.ControlPlane.Operator;
using Shouldly;
using Xunit;

namespace Scout.ControlPlane.Tests.Operator;

/// <summary>The project's format, chosen when the owner starts the project and never changed after.</summary>
public sealed partial class OperatorServiceTests
{
    private async Task<OperatorProject> ScriptReadyInAsync(string format)
    {
        await GrantYouTubeAsync();
        await _operator.StartProjectAsync("Why rivers meander", format, string.Empty, None);
        var taken = (await _operator.NextQueuedProjectAsync(None)).ShouldNotBeNull();
        return await _operator.SaveProjectFromWorkerAsync(Draft(taken, "script_ready"), null, None);
    }

    [Fact]
    public async Task StartProject_Stores_The_Format_And_The_Direction()
    {
        await GrantYouTubeAsync();

        var own = await _operator.StartProjectAsync("Why rivers meander", " own_pictures ", "  A walk along\tthe Thames.\r\nShow the bends.  ", None);
        var generated = await _operator.StartProjectAsync("Glaciers explained", "generated_pictures", "Calm and slow.", None);
        var ownVideo = await _operator.StartProjectAsync("A walk by the Thames", " own_video ", "Keep the jokes.", None);

        (own.Status, own.Format, own.Direction).ShouldBe(("queued", "own_pictures", "A walk along the Thames. \nShow the bends."));
        (generated.Format, generated.Direction).ShouldBe(("generated_pictures", "Calm and slow."));
        (ownVideo.Status, ownVideo.Format, ownVideo.Direction).ShouldBe(("recording_waiting", "own_video", "Keep the jokes."));
        var storedOwnVideo = await StoredAsync(ownVideo.Id);
        (storedOwnVideo.Status, storedOwnVideo.Format, storedOwnVideo.Direction).ShouldBe(("recording_waiting", "own_video", "Keep the jokes."));
        var storedOwn = await StoredAsync(own.Id);
        (storedOwn.Format, storedOwn.Direction).ShouldBe(("own_pictures", "A walk along the Thames. \nShow the bends."));
        var storedGenerated = await StoredAsync(generated.Id);
        (storedGenerated.Format, storedGenerated.Direction).ShouldBe(("generated_pictures", "Calm and slow."));
    }

    [Fact]
    public async Task A_Project_Started_Without_A_Format_Is_Generated_Pictures()
    {
        await GrantYouTubeAsync();

        var started = await _operator.StartProjectAsync("Why rivers meander", "  ", string.Empty, None);

        started.Format.ShouldBe("generated_pictures");
        var stored = await StoredAsync(started.Id);
        (stored.Format, stored.Direction).ShouldBe(("generated_pictures", string.Empty));
    }

    [Theory]
    [InlineData("ai_presenter")]
    [InlineData("Own_Pictures")]
    public async Task StartProject_Refuses_A_Format_Scout_Does_Not_Make_And_Stores_Nothing(string format)
    {
        await GrantYouTubeAsync();
        var logged = await _writes.ListAsync(10, None);

        var ex = await Should.ThrowAsync<OperatorValidationException>(() =>
            _operator.StartProjectAsync("Why rivers meander", format, "Calm and slow.", None));

        ex.Message.ShouldBe("The format is generated_pictures, own_pictures or own_video.");
        (await _store.ReadProjectsAsync(None)).ShouldBeEmpty();
        (await _writes.ListAsync(10, None)).ShouldBe(logged);
    }

    [Fact]
    public async Task Neither_A_Worker_Save_Nor_Produce_Nor_Delegate_Changes_The_Format()
    {
        var produced = await ScriptReadyInAsync("own_pictures");
        var delegated = await ScriptReadyInAsync("own_pictures");

        await _operator.SaveProjectFromWorkerAsync(produced with { Format = "generated_pictures", Error = "a worker save" }, null, None);
        var afterProduce = await _operator.ProduceProjectAsync(produced.Id, string.Empty, "Calm.", string.Empty, None);
        var taken = (await _operator.NextQueuedProjectAsync(None)).ShouldNotBeNull();
        var afterProducing = await _operator.SaveProjectFromWorkerAsync(
            taken with { Format = "ai_presenter", Scenes = [Scene("s1", 1)] }, null, None);
        var afterDelegate = await _operator.DelegateProjectAsync(delegated.Id, string.Empty, "Calm.", string.Empty, None);

        (afterProduce.Format, taken.Format, afterProducing.Format, afterDelegate.Format)
            .ShouldBe(("own_pictures", "own_pictures", "own_pictures", "own_pictures"));
        (await StoredAsync(produced.Id)).Format.ShouldBe("own_pictures");
        (await StoredAsync(delegated.Id)).Format.ShouldBe("own_pictures");
    }

    [Fact]
    public async Task A_Chat_Project_Keeps_The_Format_And_Direction_The_Owner_Asked_For()
    {
        await GrantYouTubeAsync();

        var own = await _operator.SaveProjectFromWorkerAsync(
            NewDraft("Glaciers explained", "researching") with { Format = " own_pictures ", Direction = "  A walk along\tthe glacier.  " }, "discord:1", None);
        var defaulted = await _operator.SaveProjectFromWorkerAsync(
            NewDraft("Why rivers meander", "researching") with { Format = string.Empty, Direction = string.Empty }, "discord:1", None);

        (own.Origin, own.Format, own.Direction).ShouldBe(("chat", "own_pictures", "A walk along the glacier."));
        var stored = await StoredAsync(own.Id);
        (stored.Format, stored.Direction).ShouldBe(("own_pictures", "A walk along the glacier."));
        var storedDefault = await StoredAsync(defaulted.Id);
        (storedDefault.Format, storedDefault.Direction).ShouldBe(("generated_pictures", string.Empty));
    }

    [Theory]
    [InlineData("ai_presenter")]
    [InlineData("Own_Pictures")]
    public async Task A_Chat_Project_In_A_Format_Scout_Does_Not_Make_Is_Refused_And_Stores_Nothing(string format)
    {
        await GrantYouTubeAsync();
        var logged = await _writes.ListAsync(10, None);

        var ex = await Should.ThrowAsync<OperatorValidationException>(() => _operator.SaveProjectFromWorkerAsync(
            NewDraft("Glaciers explained", "researching") with { Format = format, Direction = "Calm." }, "discord:1", None));

        ex.Message.ShouldBe("The format is generated_pictures, own_pictures or own_video.");
        (await _store.ReadProjectsAsync(None)).ShouldBeEmpty();
        (await _writes.ListAsync(10, None)).ShouldBe(logged);
    }

    [Theory]
    [InlineData("own_video")]
    [InlineData(" own_video ")]
    public async Task A_Chat_Project_Of_The_Owners_Own_Video_Is_Refused_And_Stores_Nothing(string format)
    {
        await GrantYouTubeAsync();
        var logged = await _writes.ListAsync(10, None);

        var ex = await Should.ThrowAsync<OperatorValidationException>(() => _operator.SaveProjectFromWorkerAsync(
            NewDraft("A walk by the Thames", "researching") with { Format = format, RecordingFile = "owner-recording-1a2b.mp4" }, "discord:1", None));

        ex.Message.ShouldBe("A video of your own recording is started in Scout Desktop, Production, where you attach the recording.");
        (await _store.ReadProjectsAsync(None)).ShouldBeEmpty();
        (await _writes.ListAsync(10, None)).ShouldBe(logged);
    }

    [Fact]
    public async Task Delegating_A_Media_Ready_Own_Pictures_Project_With_Text_Cards_Queues_Assembly()
    {
        var own = await ScriptReadyInAsync("own_pictures");
        var generated = await ScriptReadyInAsync("generated_pictures");
        await _operator.ReportReadinessAsync(new Readiness(true, true, "openai", true, true, []), None);
        Scene[] textCards = [Scene("s1", 1, visualSource: "text_card"), Scene("s2", 2, visualSource: "owner")];
        await SetStoredAsync(own.Id, p => p with { Status = "media_ready", Scenes = textCards });
        await SetStoredAsync(generated.Id, p => p with { Status = "media_ready", Scenes = textCards });

        var ownDelegated = await _operator.DelegateProjectAsync(own.Id, string.Empty, string.Empty, string.Empty, None);
        var generatedDelegated = await _operator.DelegateProjectAsync(generated.Id, string.Empty, string.Empty, string.Empty, None);

        (ownDelegated.Status, ownDelegated.Delegated).ShouldBe(("assembly_queued", true));
        (await StoredAsync(own.Id)).Status.ShouldBe("assembly_queued");
        generatedDelegated.Status.ShouldBe("production_queued");
    }
}
