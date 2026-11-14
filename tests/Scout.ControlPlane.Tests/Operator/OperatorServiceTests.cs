using System.Runtime.Versioning;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Scout.ControlPlane.Actions;
using Scout.ControlPlane.Features;
using Scout.ControlPlane.Operator;
using Scout.ControlPlane.WriteGate;
using Shouldly;
using Xunit;

namespace Scout.ControlPlane.Tests.Operator;

/// <summary>
/// The operator's state over the real catalog, write gate and Scout's own files in a temp folder.
/// </summary>
public sealed partial class OperatorServiceTests : IDisposable
{
    private const string NotAllowed =
        "The owner has not allowed youtube to write; allow it on the Features screen of Scout Desktop.";

    private readonly string _directory = Directory.CreateTempSubdirectory("scout-operator-").FullName;
    private readonly JsonFileFeatureStateStore _states;
    private readonly JsonFileActionQueue _actions;
    private readonly JsonLinesWriteLog _writes;
    private readonly JsonFileOperatorStore _store;
    private readonly FeaturesService _features;
    private readonly OperatorService _operator;

    public OperatorServiceTests()
    {
        var options = Options.Create(new FeatureStateStoreOptions { FilePath = Path.Combine(_directory, "features.json") });
        _states = new JsonFileFeatureStateStore(options, NullLogger<JsonFileFeatureStateStore>.Instance);
        _actions = new JsonFileActionQueue(options, NullLogger<JsonFileActionQueue>.Instance);
        _writes = new JsonLinesWriteLog(options, NullLogger<JsonLinesWriteLog>.Instance);
        _store = new JsonFileOperatorStore(options, NullLogger<JsonFileOperatorStore>.Instance);
        _features = new FeaturesService(
            new FeatureCatalog(),
            _states,
            new InMemoryCredentialStore(),
            new NullFeatureActivator(),
            Array.Empty<IFeatureCredentialValidator>());
        _operator = new OperatorService(_store, new WriteGateService(new FeatureCatalog(), _states, _actions, _writes), _writes);
    }

    public void Dispose()
    {
        _states.Dispose();
        _actions.Dispose();
        _writes.Dispose();
        _store.Dispose();
        Directory.Delete(_directory, recursive: true);
    }

    private Task<FeatureSnapshot> GrantYouTubeAsync() =>
        _features.SetWriteGrantAsync(FeatureCatalog.YouTubeId, granted: true, confirmWrites: true, CancellationToken.None);

    private Task<FeatureSnapshot> RevokeYouTubeAsync() =>
        _features.SetWriteGrantAsync(FeatureCatalog.YouTubeId, granted: false, confirmWrites: false, CancellationToken.None);

    private static Mandate Mandate(
        string objective = "Teach geography",
        string language = "en",
        IReadOnlyList<string>? pillars = null) =>
        new(objective, "Students", pillars ?? ["Rivers"], "weekly", language, "Calm", [], "views", "1000");

    private static Claim Claim(string id, string status, string? note = null) =>
        new(id, $"Claim {id}", "low", ["https://example.org"], status, note);

    private static OperatorProject Draft(OperatorProject project, string status, params Claim[] claims) =>
        project with { Status = status, Claims = claims };

    private static OperatorProject NewDraft(string topic, string status, params Claim[] claims) =>
        new(string.Empty, topic, status, string.Empty, default, default, [], null, claims);

    private async Task<OperatorProject> ScriptReadyAsync(params Claim[] claims)
    {
        await GrantYouTubeAsync();
        await _operator.StartProjectAsync("Why rivers meander", string.Empty, string.Empty, CancellationToken.None);
        var taken = (await _operator.NextQueuedProjectAsync(CancellationToken.None)).ShouldNotBeNull();
        return await _operator.SaveProjectFromWorkerAsync(Draft(taken, "script_ready", claims), null, CancellationToken.None);
    }

    [Fact]
    public async Task Before_The_Owner_Writes_One_The_Mandate_Is_Empty()
    {
        (await _operator.GetMandateAsync(CancellationToken.None)).ShouldBe(Scout.ControlPlane.Operator.Mandate.Empty);
    }

    [Fact]
    public async Task Saving_The_Mandate_Trims_It_Drops_Blank_Pillars_And_Stamps_The_Time()
    {
        var before = DateTimeOffset.UtcNow;

        var saved = await _operator.SaveMandateAsync(
            Mandate(objective: "  Teach geography ", language: " en ", pillars: [" Rivers ", "  ", "Mountains"]),
            CancellationToken.None);
        var read = await _operator.GetMandateAsync(CancellationToken.None);

        (read.Objective, read.Language, string.Join("|", read.Pillars)).ShouldBe(("Teach geography", "en", "Rivers|Mountains"));
        read.UpdatedAt.ShouldNotBeNull().ShouldBeInRange(before, DateTimeOffset.UtcNow);
        read.UpdatedAt.ShouldBe(saved.UpdatedAt);
    }

    [Theory]
    [InlineData(" ", "en", 1, 10, "The objective is required.")]
    [InlineData("Teach", " ", 1, 10, "The language is required.")]
    [InlineData("Teach", "en", 6, 10, "At most 5 pillars.")]
    [InlineData("Teach", "en", 1, 81, "Each pillar is at most 80 characters.")]
    public async Task A_Mandate_Outside_The_Limits_Is_Refused_And_Not_Saved(
        string objective, string language, int pillarCount, int pillarLength, string message)
    {
        var pillars = Enumerable.Range(0, pillarCount).Select(i => new string((char)('a' + i), pillarLength)).ToList();

        var ex = await Should.ThrowAsync<OperatorValidationException>(() =>
            _operator.SaveMandateAsync(Mandate(objective, language, pillars), CancellationToken.None));

        ex.Message.ShouldBe(message);
        File.Exists(_store.MandatePath).ShouldBeFalse();
    }

    [Fact]
    public async Task A_Mandate_Saved_With_A_Narration_Voice_Still_Loads_And_Saves_Without_One()
    {
        await _operator.SaveMandateAsync(Mandate(), CancellationToken.None);
        var earlier = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(_store.MandatePath))!.AsObject();
        earlier["narrationVoice"] = "piper:en_GB-cori-medium";
        await File.WriteAllTextAsync(_store.MandatePath, earlier.ToJsonString());

        var read = await _operator.GetMandateAsync(CancellationToken.None);

        (read.Objective, read.Language, string.Join("|", read.Pillars)).ShouldBe(("Teach geography", "en", "Rivers"));
        await _operator.SaveMandateAsync(read, CancellationToken.None);
        (await File.ReadAllTextAsync(_store.MandatePath)).ShouldNotContain("narrationVoice");
    }

    [Theory]
    [InlineData(true, true, "production_queued")]
    [InlineData(false, false, "assembly_queued")]
    public async Task Delegate_OnAMediaReadyProject_WithSilentScenes_QueuesProductionWhenSpeechIsAvailable_AndAssemblyWhenNothingIsAvailable(bool speech, bool images, string expected)
    {
        var project = await ScriptReadyAsync();
        await _operator.ReportReadinessAsync(new Readiness(speech, images, "openai", true, true, []), None);
        await SetStoredAsync(project.Id, p => p with
        {
            Status = "media_ready",
            Scenes = [Scene("s1", 1, audioSource: "none", visualSource: "text_card"), Scene("s2", 2, audioSource: "none", visualSource: "text_card")],
        });

        var delegated = await _operator.DelegateProjectAsync(project.Id, "piper:en_GB-cori-medium", string.Empty, string.Empty, None);

        (delegated.Status, delegated.Delegated, delegated.NarrationVoice).ShouldBe((expected, true, "piper:en_GB-cori-medium"));
        var stored = await StoredAsync(project.Id);
        (stored.Status, stored.NarrationVoice).ShouldBe((expected, "piper:en_GB-cori-medium"));
    }

    [Fact]
    public async Task Delegate_OnAMediaReadyProject_WithCompleteScenes_QueuesAssembly()
    {
        var project = await ScriptReadyAsync();
        await _operator.ReportReadinessAsync(new Readiness(true, true, "openai", true, true, []), None);
        await SetStoredAsync(project.Id, p => p with { Status = "media_ready", Scenes = [Scene("s1", 1), Scene("s2", 2)] });

        var delegated = await _operator.DelegateProjectAsync(project.Id, string.Empty, string.Empty, string.Empty, None);

        (delegated.Status, delegated.Delegated).ShouldBe(("assembly_queued", true));
    }

    [Theory]
    [InlineData("  piper:en_GB-cori-medium  ", "piper:en_GB-cori-medium")]
    [InlineData("piper:en_GB-aru-medium#3", "piper:en_GB-aru-medium#3")]
    [InlineData("OpenAI:nova", "OpenAI:nova")]
    public async Task Produce_StoresTheNarrationVoiceOnTheProject(string chosen, string kept)
    {
        var project = await ScriptReadyAsync();

        var produced = await _operator.ProduceProjectAsync(project.Id, chosen, string.Empty, string.Empty, CancellationToken.None);

        produced.NarrationVoice.ShouldBe(kept);
        (await StoredAsync(project.Id)).NarrationVoice.ShouldBe(kept);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Produce_WithAnEmptyVoice_StoresScoutsChoice(string chosen)
    {
        var project = await ScriptReadyAsync();
        await _operator.ProduceProjectAsync(project.Id, "piper:en_GB-cori-medium", string.Empty, string.Empty, CancellationToken.None);
        var taken = (await _operator.NextQueuedProjectAsync(CancellationToken.None)).ShouldNotBeNull();
        await _operator.SaveProjectFromWorkerAsync(taken with { Status = "media_ready" }, null, CancellationToken.None);

        var produced = await _operator.ProduceProjectAsync(project.Id, chosen, string.Empty, string.Empty, CancellationToken.None);

        produced.NarrationVoice.ShouldBeEmpty();
        (await StoredAsync(project.Id)).NarrationVoice.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("piper:en GB")]
    [InlineData("piper:../../etc/passwd")]
    [InlineData("OpenAI:nova;rm")]
    [InlineData("piper:voi\nce")]
    public async Task Produce_RefusesAMalformedNarrationVoice(string chosen)
    {
        var project = await ScriptReadyAsync();
        var tooLong = "piper:" + new string('a', 75);

        var malformed = await Should.ThrowAsync<OperatorValidationException>(() =>
            _operator.ProduceProjectAsync(project.Id, chosen, string.Empty, string.Empty, CancellationToken.None));
        var overLimit = await Should.ThrowAsync<OperatorValidationException>(() =>
            _operator.ProduceProjectAsync(project.Id, tooLong, string.Empty, string.Empty, CancellationToken.None));

        malformed.Message.ShouldBe("A narration voice is an id of at most 80 letters, digits, _ : . # or -.");
        overLimit.Message.ShouldBe("A narration voice is an id of at most 80 letters, digits, _ : . # or -.");
        var stored = await StoredAsync(project.Id);
        (stored.Status, stored.NarrationVoice).ShouldBe(("script_ready", string.Empty));
    }

    [Fact]
    public async Task WorkerSave_KeepsTheOwnersNarrationVoice()
    {
        var project = await ScriptReadyAsync();
        await _operator.ProduceProjectAsync(project.Id, "piper:en_GB-cori-medium", string.Empty, string.Empty, CancellationToken.None);
        var taken = (await _operator.NextQueuedProjectAsync(CancellationToken.None)).ShouldNotBeNull();

        var saved = await _operator.SaveProjectFromWorkerAsync(
            taken with { Status = "media_ready", NarrationVoice = "OpenAI:nova" }, null, CancellationToken.None);
        var created = await _operator.SaveProjectFromWorkerAsync(
            NewDraft("Glaciers explained", "researching") with { NarrationVoice = "OpenAI:nova" }, "discord:1", CancellationToken.None);

        saved.NarrationVoice.ShouldBe("piper:en_GB-cori-medium");
        (await StoredAsync(project.Id)).NarrationVoice.ShouldBe("piper:en_GB-cori-medium");
        (await StoredAsync(created.Id)).NarrationVoice.ShouldBeEmpty();
    }

    [Fact]
    public async Task Regenerate_KeepsTheNarrationVoice()
    {
        var project = await ScriptReadyAsync();
        await _operator.ProduceProjectAsync(project.Id, "piper:en_GB-aru-medium#3", string.Empty, string.Empty, CancellationToken.None);
        var taken = (await _operator.NextQueuedProjectAsync(CancellationToken.None)).ShouldNotBeNull();
        await _operator.SaveProjectFromWorkerAsync(taken with { Status = "media_ready", Scenes = [Scene("s1", 1)] }, null, CancellationToken.None);

        var regenerated = await _operator.RegenerateSceneAsync(project.Id, "s1", CancellationToken.None);

        (regenerated.Status, regenerated.NarrationVoice).ShouldBe(("production_queued", "piper:en_GB-aru-medium#3"));
        (await StoredAsync(project.Id)).NarrationVoice.ShouldBe("piper:en_GB-aru-medium#3");
    }

    [Fact]
    public async Task Five_Pillars_Of_Eighty_Characters_Are_Accepted()
    {
        var pillars = Enumerable.Range(0, 5).Select(i => new string((char)('a' + i), 80)).ToList();

        var saved = await _operator.SaveMandateAsync(Mandate(pillars: pillars), CancellationToken.None);

        saved.Pillars.ShouldBe(pillars);
    }

    [Theory]
    [InlineData("ab")]
    [InlineData("   ab   ")]
    [InlineData("")]
    public async Task A_Topic_Shorter_Than_Three_Characters_After_Trimming_Is_Refused(string topic)
    {
        var ex = await Should.ThrowAsync<OperatorValidationException>(() => _operator.StartProjectAsync(topic, string.Empty, string.Empty, CancellationToken.None));

        ex.Message.ShouldBe("The topic must be 3 to 200 characters.");
        (await _operator.ListProjectsAsync(0, CancellationToken.None)).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_Topic_Longer_Than_Two_Hundred_Characters_Is_Refused()
    {
        var ex = await Should.ThrowAsync<OperatorValidationException>(() =>
            _operator.StartProjectAsync(new string('x', 201), string.Empty, string.Empty, CancellationToken.None));

        ex.Message.ShouldBe("The topic must be 3 to 200 characters.");
    }

    [Fact]
    public async Task Without_The_Owners_Grant_The_Desktop_Cannot_Start_A_Project()
    {
        var ex = await Should.ThrowAsync<OperatorWriteRefusedException>(() =>
            _operator.StartProjectAsync("Why rivers meander", string.Empty, string.Empty, CancellationToken.None));

        ex.Message.ShouldBe(NotAllowed);
        (await _operator.ListProjectsAsync(0, CancellationToken.None)).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_Project_Started_On_The_Desktop_Is_Queued_With_Its_Trimmed_Topic()
    {
        await GrantYouTubeAsync();
        var started = await _operator.StartProjectAsync("  Why rivers meander ", string.Empty, string.Empty, CancellationToken.None);
        var read = await _operator.GetProjectAsync(started.Id, CancellationToken.None);

        (read.Topic, read.Status, read.Origin, read.Script, read.Claims.Count).ShouldBe(("Why rivers meander", "queued", "desktop", (Script?)null, 0));
    }

    [Fact]
    public async Task Projects_Are_Listed_Newest_First_Up_To_The_Limit()
    {
        await GrantYouTubeAsync();
        foreach (var topic in new[] { "First topic", "Second topic", "Third topic" })
        {
            await _operator.StartProjectAsync(topic, string.Empty, string.Empty, CancellationToken.None);
        }

        (await _operator.ListProjectsAsync(2, CancellationToken.None)).Select(p => p.Topic).ShouldBe(["Third topic", "Second topic"]);
        (await _operator.ListProjectsAsync(0, CancellationToken.None)).Select(p => p.Topic).ShouldBe(["Third topic", "Second topic", "First topic"]);
    }

    [Fact]
    public async Task A_Limit_Of_Zero_Means_Fifty_And_No_More_Than_Two_Hundred_Are_Listed()
    {
        await _store.UpdateProjectsAsync(
            projects =>
            {
                projects.AddRange(Enumerable.Range(0, 205).Select(i => new OperatorProject(
                    $"p{i}", $"Topic {i}", "queued", "desktop", default, default, [], null, [])));
                return 0;
            },
            CancellationToken.None);

        (await _operator.ListProjectsAsync(0, CancellationToken.None)).Count.ShouldBe(50);
        (await _operator.ListProjectsAsync(500, CancellationToken.None)).Count.ShouldBe(200);
    }

    [Fact]
    public async Task An_Unknown_Project_Is_Not_Found()
    {
        var ex = await Should.ThrowAsync<OperatorNotFoundException>(() => _operator.GetProjectAsync("nope", CancellationToken.None));

        ex.Message.ShouldBe("Unknown project 'nope'.");
    }

    [Fact]
    public async Task A_Claim_Is_Waived_Only_With_A_Note()
    {
        var project = await ScriptReadyAsync(Claim("c1", "pending"));

        var ex = await Should.ThrowAsync<OperatorValidationException>(() =>
            _operator.DecideClaimAsync(project.Id, "c1", "waived", "  ", CancellationToken.None));

        ex.Message.ShouldBe("A waived claim needs a note saying why.");
        (await _operator.GetProjectAsync(project.Id, CancellationToken.None)).Claims.ShouldHaveSingleItem().Status.ShouldBe("pending");
    }

    [Theory]
    [InlineData("pending")]
    [InlineData("approved")]
    [InlineData("Supported")]
    public async Task The_Owner_Decides_A_Claim_Only_As_Supported_Unsupported_Or_Waived(string status)
    {
        var project = await ScriptReadyAsync(Claim("c1", "pending"));

        var ex = await Should.ThrowAsync<OperatorValidationException>(() =>
            _operator.DecideClaimAsync(project.Id, "c1", status, "note", CancellationToken.None));

        ex.Message.ShouldBe("A claim is decided as supported, unsupported or waived.");
    }

    [Fact]
    public async Task The_Owner_Waives_A_Claim_With_A_Note_And_The_Decision_Is_Kept()
    {
        var project = await ScriptReadyAsync(Claim("c1", "pending"), Claim("c2", "unsupported"));

        var decided = await _operator.DecideClaimAsync(project.Id, "c1", "waived", " Common knowledge ", CancellationToken.None);
        var read = await _operator.GetProjectAsync(project.Id, CancellationToken.None);

        foreach (var shown in new[] { decided, read })
        {
            shown.Claims.Select(c => (c.Id, c.Status, c.Note, c.DecidedAt is not null)).ShouldBe([
                ("c1", "waived", (string?)"Common knowledge", true),
                ("c2", "unsupported", null, false),
            ]);
        }
    }

    [Fact]
    public async Task Deciding_An_Unknown_Claim_Is_Not_Found()
    {
        var project = await ScriptReadyAsync(Claim("c1", "pending"));

        var ex = await Should.ThrowAsync<OperatorNotFoundException>(() =>
            _operator.DecideClaimAsync(project.Id, "c9", "supported", null, CancellationToken.None));

        ex.Message.ShouldBe($"Unknown claim 'c9' in project '{project.Id}'.");
    }

    [Fact]
    public async Task Without_The_Owners_Grant_The_Worker_Cannot_Save_A_Project_Or_Take_A_Queued_One()
    {
        await GrantYouTubeAsync();
        var queued = await _operator.StartProjectAsync("Why rivers meander", string.Empty, string.Empty, CancellationToken.None);
        await RevokeYouTubeAsync();

        var save = await Should.ThrowAsync<OperatorWriteRefusedException>(() =>
            _operator.SaveProjectFromWorkerAsync(NewDraft("Glaciers explained", "researching"), "discord:1", CancellationToken.None));
        var update = await Should.ThrowAsync<OperatorWriteRefusedException>(() =>
            _operator.SaveProjectFromWorkerAsync(Draft(queued, "researching"), null, CancellationToken.None));
        var next = await Should.ThrowAsync<OperatorWriteRefusedException>(() => _operator.NextQueuedProjectAsync(CancellationToken.None));

        (save.Message, update.Message, next.Message).ShouldBe((NotAllowed, NotAllowed, NotAllowed));
        (await _operator.ListProjectsAsync(0, CancellationToken.None)).Select(p => (p.Topic, p.Status)).ShouldBe([("Why rivers meander", "queued")]);
    }

    [Fact]
    public async Task A_Grant_Revoked_On_The_Features_Screen_Stops_The_Next_Worker_Save()
    {
        var project = await ScriptReadyAsync(Claim("c1", "pending"));
        await RevokeYouTubeAsync();

        var ex = await Should.ThrowAsync<OperatorWriteRefusedException>(() =>
            _operator.SaveProjectFromWorkerAsync(Draft(project, "failed"), null, CancellationToken.None));

        ex.Message.ShouldBe(NotAllowed);
        (await _operator.GetProjectAsync(project.Id, CancellationToken.None)).Status.ShouldBe("script_ready");
    }

    [Theory]
    [InlineData("supported")]
    [InlineData("waived")]
    public async Task The_Worker_Cannot_Mark_A_Claim_Supported_Or_Waived(string status)
    {
        var project = await ScriptReadyAsync(Claim("c1", "pending"));

        var onExisting = await Should.ThrowAsync<OperatorTamperingException>(() =>
            _operator.SaveProjectFromWorkerAsync(Draft(project, "script_ready", Claim("c1", status, "note")), null, CancellationToken.None));
        var onNew = await Should.ThrowAsync<OperatorTamperingException>(() =>
            _operator.SaveProjectFromWorkerAsync(NewDraft("Glaciers explained", "script_ready", Claim("c1", status, "note")), null, CancellationToken.None));

        onExisting.Message.ShouldBe($"Only the owner marks a claim {status}; claim 'c1' was not.");
        onNew.Message.ShouldBe($"Only the owner marks a claim {status}; claim 'c1' was not.");
        (await _operator.GetProjectAsync(project.Id, CancellationToken.None)).Claims.ShouldHaveSingleItem().Status.ShouldBe("pending");
        (await _operator.ListProjectsAsync(0, CancellationToken.None)).ShouldHaveSingleItem();
    }

    [Theory]
    [InlineData("pending", "Common knowledge")]
    [InlineData("unsupported", "Common knowledge")]
    [InlineData("waived", "Another note")]
    public async Task The_Worker_Cannot_Overwrite_An_Owner_Decision(string status, string note)
    {
        var project = await ScriptReadyAsync(Claim("c1", "pending"));
        await _operator.DecideClaimAsync(project.Id, "c1", "waived", "Common knowledge", CancellationToken.None);

        var ex = await Should.ThrowAsync<OperatorTamperingException>(() =>
            _operator.SaveProjectFromWorkerAsync(Draft(project, "script_ready", Claim("c1", status, note)), null, CancellationToken.None));

        ex.Message.ShouldBe("Claim 'c1' carries the owner's decision; the worker may not change or drop it.");
        var kept = (await _operator.GetProjectAsync(project.Id, CancellationToken.None)).Claims.ShouldHaveSingleItem();
        (kept.Status, kept.Note).ShouldBe(("waived", "Common knowledge"));
    }

    [Fact]
    public async Task The_Worker_Cannot_Drop_A_Claim_The_Owner_Decided()
    {
        var project = await ScriptReadyAsync(Claim("c1", "pending"), Claim("c2", "pending"));
        await _operator.DecideClaimAsync(project.Id, "c1", "unsupported", null, CancellationToken.None);

        var ex = await Should.ThrowAsync<OperatorTamperingException>(() =>
            _operator.SaveProjectFromWorkerAsync(Draft(project, "script_ready", Claim("c2", "pending")), null, CancellationToken.None));

        ex.Message.ShouldBe("Claim 'c1' carries the owner's decision; the worker may not change or drop it.");
        (await _operator.GetProjectAsync(project.Id, CancellationToken.None)).Claims.Count.ShouldBe(2);
    }

    [Theory]
    [InlineData("high", "https://example.org")]
    [InlineData("low", "https://example.org/another")]
    public async Task The_Worker_Cannot_Change_The_Risk_Or_The_Sources_Of_A_Claim_The_Owner_Decided(string risk, string source)
    {
        var project = await ScriptReadyAsync(Claim("c1", "pending"));
        var decided = await _operator.DecideClaimAsync(project.Id, "c1", "supported", null, CancellationToken.None);
        var changed = decided.Claims[0] with { Risk = risk, SourceUrls = [source], DecidedAt = null };

        var ex = await Should.ThrowAsync<OperatorTamperingException>(() =>
            _operator.SaveProjectFromWorkerAsync(Draft(decided, "script_ready", changed), null, CancellationToken.None));

        ex.Message.ShouldBe("Claim 'c1' carries the owner's decision; the worker may not change or drop it.");
        var kept = (await _operator.GetProjectAsync(project.Id, CancellationToken.None)).Claims.ShouldHaveSingleItem();
        (kept.Risk, kept.Status).ShouldBe(("low", "supported"));
        kept.SourceUrls.ShouldBe(["https://example.org"]);
    }

    [Fact]
    public async Task The_Worker_Keeps_An_Owner_Decision_When_It_Sends_It_Back_Unchanged()
    {
        var project = await ScriptReadyAsync(Claim("c1", "pending"), Claim("c2", "pending"));
        var decided = await _operator.DecideClaimAsync(project.Id, "c1", "supported", null, CancellationToken.None);

        var saved = await _operator.SaveProjectFromWorkerAsync(
            Draft(decided, "failed", decided.Claims[0] with { DecidedAt = null }, Claim("c2", "unsupported")) with { Error = "ffmpeg missing" },
            null,
            CancellationToken.None);

        saved.Claims.Select(c => (c.Id, c.Status, c.DecidedAt)).ShouldBe([
            ("c1", "supported", decided.Claims[0].DecidedAt),
            ("c2", "unsupported", (DateTimeOffset?)null),
        ]);
        (saved.Status, saved.Error).ShouldBe(("failed", "ffmpeg missing"));
    }

    [Theory]
    [InlineData("Glaciers explained", "desktop")]
    [InlineData("Why rivers meander", "chat")]
    public async Task The_Worker_Cannot_Change_The_Topic_Or_Origin_Of_A_Project(string topic, string origin)
    {
        var project = await ScriptReadyAsync();

        var ex = await Should.ThrowAsync<OperatorTamperingException>(() =>
            _operator.SaveProjectFromWorkerAsync(project with { Topic = topic, Origin = origin }, null, CancellationToken.None));

        ex.Message.ShouldBe($"The worker may not change the topic or origin of project '{project.Id}'.");
        var kept = await _operator.GetProjectAsync(project.Id, CancellationToken.None);
        (kept.Topic, kept.Origin).ShouldBe(("Why rivers meander", "desktop"));
    }

    [Theory]
    [InlineData("queued")]
    [InlineData("production_queued")]
    [InlineData("published")]
    [InlineData("")]
    public async Task The_Worker_Sets_Only_Researching_Scripting_Script_Ready_Producing_Media_Ready_Or_Failed(string status)
    {
        var project = await ScriptReadyAsync();

        var ex = await Should.ThrowAsync<OperatorValidationException>(() =>
            _operator.SaveProjectFromWorkerAsync(project with { Status = status }, null, CancellationToken.None));

        ex.Message.ShouldBe($"Status '{status}' is not one the worker may set: researching, scripting, transcribing, script_ready, producing, media_ready, assembling, assembled, failed.");
        (await _operator.GetProjectAsync(project.Id, CancellationToken.None)).Status.ShouldBe("script_ready");
    }

    [Fact]
    public async Task A_Claim_With_An_Unknown_Status_Is_Refused()
    {
        await GrantYouTubeAsync();

        var ex = await Should.ThrowAsync<OperatorValidationException>(() =>
            _operator.SaveProjectFromWorkerAsync(NewDraft("Glaciers explained", "scripting", Claim("c1", "maybe")), null, CancellationToken.None));

        ex.Message.ShouldBe("Claim 'c1' has an unknown status 'maybe'.");
    }

    [Fact]
    public async Task Saving_An_Unknown_Project_Id_From_The_Worker_Is_Not_Found()
    {
        await GrantYouTubeAsync();

        var ex = await Should.ThrowAsync<OperatorNotFoundException>(() =>
            _operator.SaveProjectFromWorkerAsync(NewDraft("Glaciers explained", "scripting") with { Id = "nope" }, null, CancellationToken.None));

        ex.Message.ShouldBe("Unknown project 'nope'.");
    }

    [Fact]
    public async Task A_Project_The_Worker_Starts_Comes_From_Chat_And_Only_The_Worker_Sees_Its_Origin_Reference()
    {
        await GrantYouTubeAsync();

        var created = await _operator.SaveProjectFromWorkerAsync(
            NewDraft("  Glaciers explained ", "researching") with { Origin = "desktop" },
            "discord:channel/42",
            CancellationToken.None);
        var forDesktop = await _operator.GetProjectAsync(created.Id, CancellationToken.None);
        var listed = (await _operator.ListProjectsAsync(0, CancellationToken.None)).ShouldHaveSingleItem();
        var stored = (await _store.ReadProjectsAsync(CancellationToken.None)).ShouldHaveSingleItem();

        (created.Topic, created.Origin, created.Status).ShouldBe(("Glaciers explained", "chat", "researching"));
        stored.OriginReference.ShouldBe("discord:channel/42");
        forDesktop.OriginReference.ShouldBeNull();
        listed.OriginReference.ShouldBeNull();
    }

    [Fact]
    public async Task The_Origin_Reference_Survives_A_Worker_Update_And_Comes_Back_With_The_Queued_Project()
    {
        await GrantYouTubeAsync();
        var created = await _operator.SaveProjectFromWorkerAsync(NewDraft("Glaciers explained", "researching"), "slack:C1/171", CancellationToken.None);

        await _operator.SaveProjectFromWorkerAsync(created with { Status = "scripting", OriginReference = null }, "slack:other", CancellationToken.None);
        await _store.UpdateProjectsAsync(projects => { projects[0] = projects[0] with { Status = "queued" }; return 0; }, CancellationToken.None);
        var next = (await _operator.NextQueuedProjectAsync(CancellationToken.None)).ShouldNotBeNull();

        (next.Id, next.Status, next.OriginReference).ShouldBe((created.Id, "researching", "slack:C1/171"));
    }

    [Fact]
    public async Task The_Oldest_Queued_Project_Is_Taken_First_And_Moves_To_Researching()
    {
        await GrantYouTubeAsync();
        var first = await _operator.StartProjectAsync("First topic", string.Empty, string.Empty, CancellationToken.None);
        var second = await _operator.StartProjectAsync("Second topic", string.Empty, string.Empty, CancellationToken.None);

        var a = await _operator.NextQueuedProjectAsync(CancellationToken.None);
        var b = await _operator.NextQueuedProjectAsync(CancellationToken.None);
        var none = await _operator.NextQueuedProjectAsync(CancellationToken.None);

        (a?.Id, b?.Id, none).ShouldBe((first.Id, second.Id, (OperatorProject?)null));
        (await _operator.ListProjectsAsync(0, CancellationToken.None)).ShouldAllBe(p => p.Status == "researching");
    }

    [Fact]
    public async Task After_Every_Queued_Project_The_Oldest_Project_Left_In_Researching_Or_Scripting_For_Over_15_Minutes_Is_Taken_Again_As_Researching()
    {
        await GrantYouTubeAsync();
        var now = DateTimeOffset.UtcNow;
        OperatorProject Stored(string id, string status, TimeSpan idle, string? originReference = null) =>
            new(id, $"Topic {id}", status, "chat", now.AddHours(-3), now - idle, [], null, [], OriginReference: originReference);
        await _store.UpdateProjectsAsync(
            projects =>
            {
                projects.Add(Stored("done", "script_ready", TimeSpan.FromHours(2)));
                projects.Add(Stored("stuck-scripting", "scripting", TimeSpan.FromMinutes(20), "discord:3333:42"));
                projects.Add(Stored("live", "researching", TimeSpan.FromMinutes(5)));
                projects.Add(Stored("stuck-researching", "researching", TimeSpan.FromMinutes(16)));
                projects.Add(Stored("queued", "queued", TimeSpan.FromMinutes(1)));
                return 0;
            },
            CancellationToken.None);

        var taken = new List<OperatorProject?>();
        for (var i = 0; i < 4; i++)
        {
            taken.Add(await _operator.NextQueuedProjectAsync(CancellationToken.None));
        }

        taken.Select(p => (p?.Id, p?.Status, p?.OriginReference)).ShouldBe([
            ("queued", "researching", null),
            ("stuck-scripting", "researching", "discord:3333:42"),
            ("stuck-researching", "researching", null),
            (null, null, null),
        ]);
        taken[1].ShouldNotBeNull().UpdatedAt.ShouldBeInRange(now, DateTimeOffset.UtcNow);
        var stored = await _store.ReadProjectsAsync(CancellationToken.None);
        stored.Single(p => p.Id == "live").UpdatedAt.ShouldBe(now - TimeSpan.FromMinutes(5));
        stored.Single(p => p.Id == "done").Status.ShouldBe("script_ready");
    }

    [Fact]
    public async Task A_Poll_With_Nothing_To_Take_Leaves_The_Projects_File_Untouched()
    {
        await GrantYouTubeAsync();
        var now = DateTimeOffset.UtcNow;
        await _store.UpdateProjectsAsync(
            projects =>
            {
                projects.Add(new OperatorProject("done", "Old topic", "script_ready", "desktop", now.AddHours(-3), now.AddHours(-2), [], null, []));
                projects.Add(new OperatorProject("live", "Live topic", "scripting", "desktop", now.AddHours(-1), now.AddMinutes(-5), [], null, []));
                return 0;
            },
            CancellationToken.None);
        var written = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(_store.ProjectsPath, written);
        var bytes = await File.ReadAllBytesAsync(_store.ProjectsPath);

        var next = await _operator.NextQueuedProjectAsync(CancellationToken.None);

        next.ShouldBeNull();
        (await File.ReadAllBytesAsync(_store.ProjectsPath)).ShouldBe(bytes);
        File.GetLastWriteTimeUtc(_store.ProjectsPath).ShouldBe(written);
    }

    [Fact]
    public async Task Two_Concurrent_Workers_Never_Take_The_Same_Queued_Project()
    {
        await GrantYouTubeAsync();
        await _operator.StartProjectAsync("The only topic", string.Empty, string.Empty, CancellationToken.None);

        var taken = await Task.WhenAll(Enumerable.Range(0, 32)
            .Select(_ => Task.Run(() => _operator.NextQueuedProjectAsync(CancellationToken.None))));

        taken.OfType<OperatorProject>().ShouldHaveSingleItem().Topic.ShouldBe("The only topic");
    }

    [Fact]
    public async Task The_Worker_Reads_Projects_Newest_First_With_The_Owners_Decisions_Under_The_Grant()
    {
        var older = await ScriptReadyAsync(Claim("c1", "pending"), Claim("c2", "pending"));
        await _operator.DecideClaimAsync(older.Id, "c1", "waived", "Common knowledge", CancellationToken.None);
        var newer = await _operator.StartProjectAsync("Glaciers explained", string.Empty, string.Empty, CancellationToken.None);

        var read = await _operator.GetProjectForWorkerAsync(older.Id, CancellationToken.None);
        var listed = await _operator.ListProjectsForWorkerAsync(0, CancellationToken.None);
        var limited = await _operator.ListProjectsForWorkerAsync(1, CancellationToken.None);
        var unknown = await Should.ThrowAsync<OperatorNotFoundException>(() => _operator.GetProjectForWorkerAsync("nope", CancellationToken.None));

        read.Claims.Select(c => (c.Id, c.Status, c.Note)).ShouldBe([("c1", "waived", (string?)"Common knowledge"), ("c2", "pending", null)]);
        listed.Select(p => p.Id).ShouldBe([newer.Id, older.Id]);
        limited.ShouldHaveSingleItem().Id.ShouldBe(newer.Id);
        unknown.Message.ShouldBe("Unknown project 'nope'.");
    }

    [Fact]
    public async Task Without_The_Owners_Grant_The_Worker_Cannot_Read_Projects()
    {
        await GrantYouTubeAsync();
        var queued = await _operator.StartProjectAsync("Why rivers meander", string.Empty, string.Empty, CancellationToken.None);
        await RevokeYouTubeAsync();

        var get = await Should.ThrowAsync<OperatorWriteRefusedException>(() => _operator.GetProjectForWorkerAsync(queued.Id, CancellationToken.None));
        var list = await Should.ThrowAsync<OperatorWriteRefusedException>(() => _operator.ListProjectsForWorkerAsync(0, CancellationToken.None));

        (get.Message, list.Message).ShouldBe((NotAllowed, NotAllowed));
    }

    [Fact]
    public async Task Each_Worker_Save_And_Each_Taken_Project_Appends_One_Write_Log_Entry_And_A_Refused_Save_Or_An_Idle_Poll_None()
    {
        var project = await ScriptReadyAsync(Claim("c1", "pending"));
        var afterFirstSave = await _writes.ListAsync(10, CancellationToken.None);

        await Should.ThrowAsync<OperatorTamperingException>(() =>
            _operator.SaveProjectFromWorkerAsync(Draft(project, "script_ready", Claim("c1", "supported")), null, CancellationToken.None));
        await Should.ThrowAsync<OperatorValidationException>(() =>
            _operator.SaveProjectFromWorkerAsync(project with { Status = "published" }, null, CancellationToken.None));
        (await _operator.NextQueuedProjectAsync(CancellationToken.None)).ShouldBeNull();
        var afterRefusals = await _writes.ListAsync(10, CancellationToken.None);
        var created = await _operator.SaveProjectFromWorkerAsync(NewDraft("Glaciers explained", "researching"), "slack:C1", CancellationToken.None);
        var afterCreate = await _writes.ListAsync(10, CancellationToken.None);

        afterFirstSave.Select(e => (e.FeatureId, e.Action, e.Target, e.Outcome, e.ApprovedBy, e.ActionId)).ShouldBe([
            ("youtube", "Scout's data folder", project.Id, "saved script_ready", (string?)null, (string?)null),
            ("youtube", "Scout's data folder", project.Id, "saved researching", null, null),
        ]);
        afterRefusals.Count.ShouldBe(2);
        afterCreate.Select(e => (e.Target, e.Outcome)).ShouldBe([
            (created.Id, "saved researching"),
            (project.Id, "saved script_ready"),
            (project.Id, "saved researching"),
        ]);
    }

    private static Scene Scene(
        string id,
        int order,
        bool locked = false,
        string status = "ready",
        string audioSource = "generated",
        string visualSource = "generated",
        string? audioFile = null,
        string? visualFile = null) =>
        new(id, order, $"Narration {id}", $"Prompt {id}", locked, status, null, audioSource, visualSource,
            audioFile ?? (audioSource == "none" ? null : $"{id}.mp3"), visualFile ?? $"{id}.png", 3000);

    private async Task<OperatorProject> WithScenesAsync(params Scene[] scenes)
    {
        var project = await TakenForProductionAsync();
        return await _operator.SaveProjectFromWorkerAsync(project with { Status = "media_ready", Scenes = scenes }, null, CancellationToken.None);
    }

    private async Task<OperatorProject> ProducingWithScenesAsync(params Scene[] scenes)
    {
        var project = await TakenForProductionAsync();
        return await _operator.SaveProjectFromWorkerAsync(project with { Scenes = scenes }, null, CancellationToken.None);
    }

    private async Task<OperatorProject> TakenForProductionAsync()
    {
        var project = await ScriptReadyAsync();
        await _operator.ProduceProjectAsync(project.Id, string.Empty, string.Empty, string.Empty, CancellationToken.None);
        var taken = (await _operator.NextQueuedProjectAsync(CancellationToken.None)).ShouldNotBeNull();
        taken.Status.ShouldBe("producing");
        return taken;
    }

    private async Task<OperatorProject> StoredAsync(string id) =>
        (await _store.ReadProjectsAsync(CancellationToken.None)).Single(p => p.Id == id);

    [Fact]
    public async Task Next_Takes_Queued_Projects_Then_Projects_Queued_For_Production_As_Producing_Then_Stale_Ones()
    {
        await GrantYouTubeAsync();
        var now = DateTimeOffset.UtcNow;
        OperatorProject Stored(string id, string status, TimeSpan idle) =>
            new(id, $"Topic {id}", status, "desktop", now.AddHours(-3), now - idle, [], null, []);
        await _store.UpdateProjectsAsync(
            projects =>
            {
                projects.Add(Stored("stuck-producing", "producing", TimeSpan.FromMinutes(30)));
                projects.Add(Stored("live-producing", "producing", TimeSpan.FromMinutes(2)));
                projects.Add(Stored("produce-first", "production_queued", TimeSpan.FromMinutes(1)));
                projects.Add(Stored("stuck-scripting", "scripting", TimeSpan.FromMinutes(20)));
                projects.Add(Stored("produce-second", "production_queued", TimeSpan.FromMinutes(1)));
                projects.Add(Stored("queued", "queued", TimeSpan.FromMinutes(1)));
                return 0;
            },
            CancellationToken.None);

        var taken = new List<OperatorProject?>();
        for (var i = 0; i < 6; i++)
        {
            taken.Add(await _operator.NextQueuedProjectAsync(CancellationToken.None));
        }

        taken.Select(p => (p?.Id, p?.Status)).ShouldBe([
            ("queued", "researching"),
            ("produce-first", "producing"),
            ("produce-second", "producing"),
            ("stuck-producing", "producing"),
            ("stuck-scripting", "researching"),
            (null, null),
        ]);
        (await StoredAsync("live-producing")).UpdatedAt.ShouldBe(now - TimeSpan.FromMinutes(2));
        (await _writes.ListAsync(10, CancellationToken.None)).Take(5).Select(e => (e.Target, e.Outcome)).Reverse().ShouldBe([
            ("queued", "saved researching"),
            ("produce-first", "saved producing"),
            ("produce-second", "saved producing"),
            ("stuck-producing", "saved producing"),
            ("stuck-scripting", "saved researching"),
        ]);
    }

    [Fact]
    public async Task The_Worker_Saves_Producing_And_Media_Ready_On_A_Project_It_Took_For_Production()
    {
        var project = await TakenForProductionAsync();

        var producing = await _operator.SaveProjectFromWorkerAsync(project with { Status = "producing" }, null, CancellationToken.None);
        var ready = await _operator.SaveProjectFromWorkerAsync(project with { Status = "media_ready" }, null, CancellationToken.None);

        (producing.Status, ready.Status).ShouldBe(("producing", "media_ready"));
    }

    [Theory]
    [InlineData("producing")]
    [InlineData("media_ready")]
    [InlineData("scene")]
    public async Task A_Regenerate_Between_The_Workers_Read_And_Its_Save_Refuses_The_Save_And_Keeps_The_Owners_Regenerate(string sent)
    {
        var project = await ProducingWithScenesAsync(Scene("s1", 1, status: "planned"), Scene("s2", 2));
        var read = await _operator.GetProjectForWorkerAsync(project.Id, CancellationToken.None);
        var regenerated = await _operator.RegenerateSceneAsync(project.Id, "s2", CancellationToken.None);
        var save = sent switch
        {
            "scene" => read with { Status = "producing", Scenes = [read.Scenes[0] with { Status = "ready" }, read.Scenes[1]] },
            _ => read with { Status = sent },
        };

        var ex = await Should.ThrowAsync<OperatorTamperingException>(() =>
            _operator.SaveProjectFromWorkerAsync(save, null, CancellationToken.None));

        ex.Message.ShouldBe("The project is no longer producing.");
        var stored = await StoredAsync(project.Id);
        stored.Status.ShouldBe("production_queued");
        stored.Scenes.ShouldBe(regenerated.Scenes);
    }

    [Theory]
    [InlineData("failed")]
    [InlineData("script_ready")]
    public async Task Any_Worker_Save_On_A_Project_The_Owner_Queued_For_Production_Again_Is_Refused_And_Keeps_It_Queued(string sent)
    {
        var project = await ProducingWithScenesAsync(Scene("s1", 1));
        var read = await _operator.GetProjectForWorkerAsync(project.Id, CancellationToken.None);
        await _operator.SaveProjectFromWorkerAsync(read with { Status = "media_ready" }, null, CancellationToken.None);
        await _operator.ProduceProjectAsync(project.Id, string.Empty, string.Empty, string.Empty, CancellationToken.None);

        var ex = await Should.ThrowAsync<OperatorTamperingException>(() =>
            _operator.SaveProjectFromWorkerAsync(read with { Status = sent, Error = "Speech timed out." }, null, CancellationToken.None));

        ex.Message.ShouldBe("The project is no longer producing.");
        var stored = await StoredAsync(project.Id);
        (stored.Status, stored.Error).ShouldBe(("production_queued", (string?)null));
    }

    [Fact]
    public async Task An_Owner_Reorder_Lock_And_Attach_Between_The_Workers_Read_And_Its_Save_Are_Kept_And_The_Save_Is_Accepted()
    {
        var project = await ProducingWithScenesAsync(
            Scene("s1", 1, status: "planned", audioSource: "none", visualSource: "text_card") with { VisualFile = null },
            Scene("s2", 2, status: "planned", audioSource: "none", visualSource: "text_card") with { VisualFile = null },
            Scene("s3", 3, status: "planned", audioSource: "none", visualSource: "text_card") with { VisualFile = null });
        var media = await _operator.GetMediaDirectoryAsync(project.Id, CancellationToken.None);
        await File.WriteAllTextAsync(Path.Combine(media, "owner-s1.png"), "image");
        var read = await _operator.GetProjectForWorkerAsync(project.Id, CancellationToken.None);
        await _operator.ReorderScenesAsync(project.Id, ["s3", "s1", "s2"], CancellationToken.None);
        await _operator.SetSceneLockedAsync(project.Id, "s2", true, CancellationToken.None);
        var owned = await _operator.AttachSceneMediaAsync(project.Id, "s1", "visual", "owner-s1.png", CancellationToken.None);
        static Scene Produced(Scene s) => s with
        {
            Status = "ready",
            AudioSource = "generated",
            AudioFile = $"scene-{s.Id}.mp3",
            VisualSource = "generated",
            VisualFile = $"scene-{s.Id}.png",
            DurationMs = 2400,
        };

        var saved = await _operator.SaveProjectFromWorkerAsync(
            read with { Scenes = [.. read.Scenes.Select(Produced)] }, null, CancellationToken.None);

        saved.Status.ShouldBe("producing");
        saved.Scenes.Select(s => (s.Id, s.Order, s.Locked, s.Status, s.AudioSource, s.AudioFile, s.VisualSource, s.VisualFile)).ShouldBe([
            ("s3", 1, false, "ready", "generated", (string?)"scene-s3.mp3", "generated", (string?)"scene-s3.png"),
            ("s1", 2, false, "ready", "generated", (string?)"scene-s1.mp3", "owner", (string?)"owner-s1.png"),
            ("s2", 3, true, "planned", "none", (string?)null, "text_card", (string?)null),
        ]);
        saved.Scenes.Single(s => s.Id == "s2").ShouldBe(owned.Scenes.Single(s => s.Id == "s2"));
        (await StoredAsync(project.Id)).Scenes.ShouldBe(saved.Scenes);
    }

    [Fact]
    public async Task Producing_A_Script_Ready_Project_With_Every_Claim_Decided_Queues_It_For_Production()
    {
        var project = await ScriptReadyAsync(Claim("c1", "pending"), Claim("c2", "unsupported"));
        await _operator.DecideClaimAsync(project.Id, "c1", "supported", null, CancellationToken.None);

        var produced = await _operator.ProduceProjectAsync(project.Id, string.Empty, string.Empty, string.Empty, CancellationToken.None);

        produced.Status.ShouldBe("production_queued");
        (await StoredAsync(project.Id)).Status.ShouldBe("production_queued");
    }

    [Fact]
    public async Task A_Media_Ready_Project_Is_Produced_Again()
    {
        var project = await WithScenesAsync(Scene("s1", 1));

        (await _operator.ProduceProjectAsync(project.Id, string.Empty, string.Empty, string.Empty, CancellationToken.None)).Status.ShouldBe("production_queued");
    }

    [Fact]
    public async Task Produce_OnAnAssembledProject_QueuesProductionAndClearsApproval()
    {
        var project = await ReadyToApproveAsync();
        await _operator.ConfirmMediaRightsAsync(project.Id, None);

        var produced = await _operator.ProduceProjectAsync(project.Id, string.Empty, string.Empty, string.Empty, None);

        var package = produced.Package.ShouldNotBeNull();
        (produced.Status, package.RightsConfirmed, package.ApprovedAt, package.VideoFile)
            .ShouldBe(("production_queued", false, (DateTimeOffset?)null, (string?)"video.mp4"));
    }

    [Fact]
    public async Task Produce_OnAnApprovedProject_ClearsApprovalAndRights()
    {
        var project = await ReadyToApproveAsync();
        await _operator.ApprovePackageAsync(project.Id, None);

        var produced = await _operator.ProduceProjectAsync(project.Id, string.Empty, string.Empty, string.Empty, None);

        var package = produced.Package.ShouldNotBeNull();
        (produced.Status, package.RightsConfirmed, package.ApprovedAt).ShouldBe(("production_queued", false, (DateTimeOffset?)null));
        (await StoredAsync(project.Id)).Package!.ApprovedAt.ShouldBeNull();
    }

    [Theory]
    [InlineData("assembly_queued")]
    [InlineData("assembling")]
    public async Task Produce_IsRefusedWhileAssembling(string status)
    {
        var project = await ReadyToApproveAsync();
        await SetStoredAsync(project.Id, p => p with { Status = status });

        var ex = await Should.ThrowAsync<OperatorValidationException>(() => _operator.ProduceProjectAsync(project.Id, string.Empty, string.Empty, string.Empty, None));

        ex.Message.ShouldBe($"Only a project with its script ready, its media ready, an assembled or approved one, or a failed one with a script goes to production; this one is {status}.");
        (await StoredAsync(project.Id)).Status.ShouldBe(status);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public async Task A_Failed_Project_With_A_Script_And_No_Pending_Claim_Is_Produced_Again_Without_Its_Old_Error(int scenes)
    {
        var taken = await TakenForProductionAsync();
        var failed = await _operator.SaveProjectFromWorkerAsync(
            taken with
            {
                Status = "failed",
                Script = new Script("Rivers bend.", [new ScriptSection("Meanders", "Water erodes the outer bank.")], "Subscribe.", [], string.Empty, [], []),
                Error = "The AI model did not return a picture prompt for every scene in the expected form, twice.",
                Scenes = [.. Enumerable.Range(1, scenes).Select(i => Scene($"s{i}", i))],
            },
            null,
            CancellationToken.None);

        var produced = await _operator.ProduceProjectAsync(failed.Id, string.Empty, string.Empty, string.Empty, CancellationToken.None);

        (failed.Status, failed.Error).ShouldBe(("failed", "The AI model did not return a picture prompt for every scene in the expected form, twice."));
        failed.Script.ShouldNotBeNull().Hook.ShouldBe("Rivers bend.");
        (produced.Status, produced.Error).ShouldBe(("production_queued", (string?)null));
        var stored = await StoredAsync(failed.Id);
        (stored.Status, stored.Error).ShouldBe(("production_queued", (string?)null));
    }

    [Fact]
    public async Task A_Failed_Project_Without_A_Script_Or_With_A_Pending_Claim_Is_Not_Produced()
    {
        await GrantYouTubeAsync();
        await _operator.StartProjectAsync("Why rivers meander", string.Empty, string.Empty, CancellationToken.None);
        var researching = (await _operator.NextQueuedProjectAsync(CancellationToken.None)).ShouldNotBeNull();
        var noScript = await _operator.SaveProjectFromWorkerAsync(researching with { Status = "failed", Error = "No search." }, null, CancellationToken.None);
        var pending = await ScriptReadyAsync(Claim("c1", "pending"));
        pending = await _operator.SaveProjectFromWorkerAsync(
            pending with { Status = "failed", Script = new Script("Rivers bend.", [], "Subscribe.", [], string.Empty, [], []), Error = "Stopped." },
            null,
            CancellationToken.None);

        var withoutScript = await Should.ThrowAsync<OperatorValidationException>(() => _operator.ProduceProjectAsync(noScript.Id, string.Empty, string.Empty, string.Empty, CancellationToken.None));
        var withPending = await Should.ThrowAsync<OperatorValidationException>(() => _operator.ProduceProjectAsync(pending.Id, string.Empty, string.Empty, string.Empty, CancellationToken.None));

        withoutScript.Message.ShouldBe("Only a project with its script ready, its media ready, an assembled or approved one, or a failed one with a script goes to production; this one is failed.");
        withPending.Message.ShouldBe("1 claim is still pending; decide it before production.");
        ((await StoredAsync(noScript.Id)).Status, (await StoredAsync(pending.Id)).Status).ShouldBe(("failed", "failed"));
    }

    [Theory]
    [InlineData(1, "1 claim is still pending; decide it before production.")]
    [InlineData(2, "2 claims are still pending; decide them before production.")]
    public async Task A_Project_With_Claims_Still_Pending_Is_Not_Produced_And_The_Refusal_Says_How_Many(int pending, string message)
    {
        var claims = Enumerable.Range(1, pending).Select(i => Claim($"c{i}", "pending")).Append(Claim("c9", "unsupported")).ToArray();
        var project = await ScriptReadyAsync(claims);

        var ex = await Should.ThrowAsync<OperatorValidationException>(() => _operator.ProduceProjectAsync(project.Id, string.Empty, string.Empty, string.Empty, CancellationToken.None));

        ex.Message.ShouldBe(message);
        (await StoredAsync(project.Id)).Status.ShouldBe("script_ready");
    }

    [Fact]
    public async Task Only_A_Script_Ready_Or_Media_Ready_Project_Is_Produced()
    {
        await GrantYouTubeAsync();
        var queued = await _operator.StartProjectAsync("Why rivers meander", string.Empty, string.Empty, CancellationToken.None);

        var ex = await Should.ThrowAsync<OperatorValidationException>(() => _operator.ProduceProjectAsync(queued.Id, string.Empty, string.Empty, string.Empty, CancellationToken.None));

        ex.Message.ShouldBe("Only a project with its script ready, its media ready, an assembled or approved one, or a failed one with a script goes to production; this one is queued.");
        (await StoredAsync(queued.Id)).Status.ShouldBe("queued");
    }

    [Fact]
    public async Task Without_The_Owners_Grant_A_Project_Is_Not_Produced()
    {
        var project = await ScriptReadyAsync();
        await RevokeYouTubeAsync();

        var ex = await Should.ThrowAsync<OperatorWriteRefusedException>(() => _operator.ProduceProjectAsync(project.Id, string.Empty, string.Empty, string.Empty, CancellationToken.None));

        ex.Message.ShouldBe(NotAllowed);
        (await StoredAsync(project.Id)).Status.ShouldBe("script_ready");
    }

    [Fact]
    public async Task Reordering_Scenes_With_Every_Scene_Once_Numbers_Them_In_The_Owners_Order()
    {
        var project = await WithScenesAsync(Scene("s1", 1), Scene("s2", 2), Scene("s3", 3));

        var reordered = await _operator.ReorderScenesAsync(project.Id, ["s3", "s1", "s2"], CancellationToken.None);

        reordered.Scenes.Select(s => (s.Id, s.Order)).ShouldBe([("s3", 1), ("s1", 2), ("s2", 3)]);
        (await StoredAsync(project.Id)).Scenes.Select(s => (s.Id, s.Order)).ShouldBe([("s3", 1), ("s1", 2), ("s2", 3)]);
    }

    [Theory]
    [InlineData("s1,s2")]
    [InlineData("s1,s2,s2")]
    [InlineData("s1,s2,s9")]
    [InlineData("s1,s2,s3,s9")]
    public async Task An_Order_That_Is_Not_Exactly_The_Projects_Scenes_Is_Refused_And_Changes_Nothing(string ids)
    {
        var project = await WithScenesAsync(Scene("s1", 1), Scene("s2", 2), Scene("s3", 3));

        var ex = await Should.ThrowAsync<OperatorValidationException>(() =>
            _operator.ReorderScenesAsync(project.Id, ids.Split(','), CancellationToken.None));

        ex.Message.ShouldBe("The new order must name every scene of the project exactly once.");
        (await StoredAsync(project.Id)).Scenes.Select(s => (s.Id, s.Order)).ShouldBe([("s1", 1), ("s2", 2), ("s3", 3)]);
    }

    [Fact]
    public async Task The_Owner_Locks_And_Unlocks_A_Scene_And_An_Unknown_Scene_Is_Not_Found()
    {
        var project = await WithScenesAsync(Scene("s1", 1), Scene("s2", 2));

        var locked = await _operator.SetSceneLockedAsync(project.Id, "s2", true, CancellationToken.None);
        var unlocked = await _operator.SetSceneLockedAsync(project.Id, "s2", false, CancellationToken.None);
        var unknown = await Should.ThrowAsync<OperatorNotFoundException>(() =>
            _operator.SetSceneLockedAsync(project.Id, "s9", true, CancellationToken.None));

        locked.Scenes.Select(s => s.Locked).ShouldBe([false, true]);
        unlocked.Scenes.Select(s => s.Locked).ShouldBe([false, false]);
        unknown.Message.ShouldBe($"Unknown scene 's9' in project '{project.Id}'.");
    }

    [Fact]
    public async Task Regenerating_A_Locked_Scene_Is_Refused_And_Changes_Nothing()
    {
        var project = await WithScenesAsync(Scene("s1", 1));
        await _operator.SetSceneLockedAsync(project.Id, "s1", true, CancellationToken.None);

        var ex = await Should.ThrowAsync<OperatorValidationException>(() =>
            _operator.RegenerateSceneAsync(project.Id, "s1", CancellationToken.None));

        ex.Message.ShouldBe("Scene 's1' is locked; unlock it before regenerating it.");
        var stored = await StoredAsync(project.Id);
        (stored.Status, stored.Scenes[0].Status, stored.Scenes[0].AudioFile).ShouldBe(("media_ready", "ready", "s1.mp3"));
    }

    [Fact]
    public async Task Regenerating_A_Scene_Plans_It_Again_Keeps_The_Owners_Media_Drops_The_Generated_Files_And_Queues_Production()
    {
        var project = await WithScenesAsync(Scene("s1", 1), Scene("s2", 2, status: "failed") with { Error = "Speech timed out." });
        await _store.UpdateProjectsAsync(
            projects =>
            {
                var index = projects.FindIndex(p => p.Id == project.Id);
                projects[index] = projects[index] with
                {
                    Scenes = [projects[index].Scenes[0], projects[index].Scenes[1] with { VisualSource = "owner", VisualFile = "mine.png" }],
                    OriginReference = "discord:1",
                };
                return 0;
            },
            CancellationToken.None);

        var regenerated = await _operator.RegenerateSceneAsync(project.Id, "s2", CancellationToken.None);

        var scene = regenerated.Scenes[1];
        (scene.Status, scene.Error, scene.AudioSource, scene.AudioFile, scene.VisualSource, scene.VisualFile, scene.DurationMs)
            .ShouldBe(("planned", (string?)null, "generated", (string?)null, "owner", (string?)"mine.png", 0));
        regenerated.Scenes[0].ShouldBe(project.Scenes[0]);
        regenerated.Status.ShouldBe("production_queued");
        regenerated.OriginReference.ShouldBeNull();
        (await StoredAsync(project.Id)).OriginReference.ShouldBe("discord:1");
    }

    [Fact]
    public async Task Attaching_The_Owners_Visual_Marks_It_As_The_Owners_And_A_Scene_Production_Left_Without_Audio_Is_Ready()
    {
        var project = await WithScenesAsync(Scene("s1", 1, audioSource: "none", visualSource: "text_card") with { VisualFile = null, Error = "No narration." });
        var media = await _operator.GetMediaDirectoryAsync(project.Id, CancellationToken.None);
        await File.WriteAllTextAsync(Path.Combine(media, "my map.webp"), "image");

        var attached = await _operator.AttachSceneMediaAsync(project.Id, "s1", "visual", "my map.webp", CancellationToken.None);

        var scene = attached.Scenes.ShouldHaveSingleItem();
        (scene.VisualSource, scene.VisualFile, scene.Status, scene.Error).ShouldBe(("owner", (string?)"my map.webp", "ready", (string?)null));
    }

    [Theory]
    [InlineData("planned")]
    [InlineData("failed")]
    public async Task An_Image_Attached_To_A_Scene_Production_Has_Not_Made_Keeps_It_Not_Ready_Because_Its_Audio_Is_Not_Decided(string status)
    {
        var project = await WithScenesAsync(Scene("s1", 1, status: status, audioSource: "none", visualSource: "text_card") with { VisualFile = null });
        var media = await _operator.GetMediaDirectoryAsync(project.Id, CancellationToken.None);
        await File.WriteAllTextAsync(Path.Combine(media, "my map.webp"), "image");

        var attached = await _operator.AttachSceneMediaAsync(project.Id, "s1", "visual", "my map.webp", CancellationToken.None);

        var scene = attached.Scenes.ShouldHaveSingleItem();
        (scene.VisualSource, scene.VisualFile, scene.AudioSource, scene.Status).ShouldBe(("owner", (string?)"my map.webp", "none", status));
    }

    [Fact]
    public async Task The_Owners_Audio_On_A_Text_Card_Scene_Production_Made_Is_Ready()
    {
        var project = await WithScenesAsync(Scene("s1", 1, audioSource: "none", visualSource: "text_card") with { VisualFile = null });
        var media = await _operator.GetMediaDirectoryAsync(project.Id, CancellationToken.None);
        await File.WriteAllTextAsync(Path.Combine(media, "voice.mp3"), "audio");

        var attached = await _operator.AttachSceneMediaAsync(project.Id, "s1", "audio", "voice.mp3", CancellationToken.None);

        var scene = attached.Scenes.ShouldHaveSingleItem();
        (scene.AudioSource, scene.AudioFile, scene.VisualSource, scene.Status).ShouldBe(("owner", (string?)"voice.mp3", "text_card", "ready"));
    }

    [Fact]
    public async Task Attaching_The_Owners_Audio_Sets_The_Duration_To_Unknown()
    {
        var project = await WithScenesAsync(Scene("s1", 1));
        var media = await _operator.GetMediaDirectoryAsync(project.Id, CancellationToken.None);
        await File.WriteAllTextAsync(Path.Combine(media, "voice.mp3"), "audio");

        var attached = await _operator.AttachSceneMediaAsync(project.Id, "s1", "audio", "voice.mp3", CancellationToken.None);

        project.Scenes[0].DurationMs.ShouldBe(3000);
        attached.Scenes.ShouldHaveSingleItem().DurationMs.ShouldBe(0);
    }

    [Fact]
    public async Task Attaching_The_Owners_Audio_To_A_Scene_Without_A_Visual_Leaves_It_Not_Ready()
    {
        var project = await WithScenesAsync(Scene("s1", 1, status: "planned", audioSource: "none") with { VisualFile = null });
        var media = await _operator.GetMediaDirectoryAsync(project.Id, CancellationToken.None);
        await File.WriteAllTextAsync(Path.Combine(media, "take2.m4a"), "audio");

        var attached = await _operator.AttachSceneMediaAsync(project.Id, "s1", "audio", "take2.m4a", CancellationToken.None);

        var scene = attached.Scenes.ShouldHaveSingleItem();
        (scene.AudioSource, scene.AudioFile, scene.Status).ShouldBe(("owner", (string?)"take2.m4a", "planned"));
    }

    [Fact]
    public async Task Media_Is_Attached_Only_From_A_File_Already_In_The_Projects_Media_Folder()
    {
        var project = await WithScenesAsync(Scene("s1", 1));
        await _operator.GetMediaDirectoryAsync(project.Id, CancellationToken.None);

        var ex = await Should.ThrowAsync<OperatorValidationException>(() =>
            _operator.AttachSceneMediaAsync(project.Id, "s1", "audio", "missing.mp3", CancellationToken.None));

        ex.Message.ShouldBe("'missing.mp3' is not in the project's media folder; copy it there first.");
        (await StoredAsync(project.Id)).Scenes[0].AudioSource.ShouldBe("generated");
    }

    [Theory]
    [InlineData("audio", "../x.mp3")]
    [InlineData("visual", "a/b.png")]
    [InlineData("visual", "a\\b.png")]
    [InlineData("audio", ".x.mp3")]
    [InlineData("audio", "a..b.mp3")]
    [InlineData("audio", "")]
    [InlineData("audio", "voice.exe")]
    [InlineData("visual", "voice.mp3")]
    [InlineData("audio", "121")]
    public async Task An_Unsafe_Or_Foreign_File_Name_Is_Refused_Even_When_The_File_Exists(string kind, string name)
    {
        var fileName = name == "121" ? new string('a', 117) + ".mp3" : name;
        var project = await WithScenesAsync(Scene("s1", 1));
        var media = await _operator.GetMediaDirectoryAsync(project.Id, CancellationToken.None);
        Directory.CreateDirectory(Path.Combine(media, "a"));
        foreach (var existing in new[] { "../x.mp3", "a/b.png", "a\\b.png", ".x.mp3", "a..b.mp3", "voice.exe", "voice.mp3" })
        {
            await File.WriteAllTextAsync(Path.Combine(media, existing), "media");
        }

        var ex = await Should.ThrowAsync<OperatorValidationException>(() =>
            _operator.AttachSceneMediaAsync(project.Id, "s1", kind, fileName, CancellationToken.None));

        var (article, extensions) = kind == "audio" ? ("an audio", ".mp3, .wav, .m4a, .ogg") : ("a visual", ".png, .jpg, .jpeg, .webp");
        ex.Message.ShouldBe($"'{fileName}' is not {article} file Scout accepts: a plain file name of at most 120 characters, without a folder or a leading dot, ending in {extensions}.");
        (await StoredAsync(project.Id)).Scenes[0].ShouldBe(project.Scenes[0]);
    }

    [Fact]
    public async Task A_File_Name_Of_120_Characters_Is_Accepted()
    {
        var fileName = new string('a', 116) + ".mp3";
        var project = await WithScenesAsync(Scene("s1", 1));
        var media = await _operator.GetMediaDirectoryAsync(project.Id, CancellationToken.None);
        await File.WriteAllTextAsync(Path.Combine(media, fileName), "audio");

        var attached = await _operator.AttachSceneMediaAsync(project.Id, "s1", "audio", fileName, CancellationToken.None);

        attached.Scenes[0].AudioFile.ShouldBe(fileName);
    }

    [Fact]
    public async Task Media_Is_Attached_Only_As_Audio_Visual_Background_Or_Recording()
    {
        var project = await WithScenesAsync(Scene("s1", 1));

        var ex = await Should.ThrowAsync<OperatorValidationException>(() =>
            _operator.AttachSceneMediaAsync(project.Id, "s1", "music", "song.mp3", CancellationToken.None));

        ex.Message.ShouldBe("Media is attached as audio, visual, background or recording.");
    }

    [Theory]
    [InlineData("script_ready", "script_ready")]
    [InlineData("media_ready", "media_ready")]
    [InlineData("failed", "failed")]
    [InlineData("assembly_queued", "assembly_queued")]
    [InlineData("assembling", "assembly_queued")]
    public async Task AttachBackground_StoresTheFileOnTheProject(string status, string expected)
    {
        var project = await ScriptReadyAsync();
        await PutInMediaAsync(project.Id, "studio wall.jpg");
        await SetStoredAsync(project.Id, p => p with { Status = status, OriginReference = "discord:1" });
        var before = await StoredAsync(project.Id);

        var attached = await _operator.AttachSceneMediaAsync(project.Id, string.Empty, "background", "studio wall.jpg", None);

        (attached.BackgroundFile, attached.Status, attached.OriginReference).ShouldBe(("studio wall.jpg", expected, (string?)null));
        var stored = await StoredAsync(project.Id);
        stored.BackgroundFile.ShouldBe("studio wall.jpg");
        stored.Status.ShouldBe(expected);
        stored.UpdatedAt.ShouldBeGreaterThan(before.UpdatedAt);
    }

    [Theory]
    [InlineData("queued")]
    [InlineData("researching")]
    [InlineData("scripting")]
    [InlineData("production_queued")]
    [InlineData("producing")]
    public async Task AttachBackground_IsRefusedWhileProducing(string status)
    {
        var project = await ScriptReadyAsync();
        await PutInMediaAsync(project.Id, "studio.png");
        await SetStoredAsync(project.Id, p => p with { Status = status });

        var ex = await Should.ThrowAsync<OperatorValidationException>(() =>
            _operator.AttachSceneMediaAsync(project.Id, string.Empty, "background", "studio.png", None));

        ex.Message.ShouldBe(
            $"A background is attached to a project whose script or media is ready, an assembled or approved one, one queued for assembly or assembling, or a failed one; this one is {status}.");
        (await StoredAsync(project.Id)).BackgroundFile.ShouldBeNull();
    }

    [Fact]
    public async Task AttachBackground_OnAnAssembledProject_ReturnsItToMediaReadyWithoutApproval()
    {
        var project = await AssembledAsync();
        await PutInMediaAsync(project.Id, "studio.png");

        var attached = await _operator.AttachSceneMediaAsync(project.Id, string.Empty, "background", "studio.png", None);

        var package = attached.Package.ShouldNotBeNull();
        (attached.Status, attached.BackgroundFile, package.Status, package.RightsConfirmed, package.ApprovedAt, package.VideoFile)
            .ShouldBe(("media_ready", "studio.png", "assembled", false, (DateTimeOffset?)null, (string?)"video.mp4"));
    }

    [Fact]
    public async Task AttachBackground_OnAnApprovedProject_ClearsApprovalAndRights()
    {
        var project = await ReadyToApproveAsync();
        await _operator.ApprovePackageAsync(project.Id, None);
        await PutInMediaAsync(project.Id, "studio.png");

        var attached = await _operator.AttachSceneMediaAsync(project.Id, string.Empty, "background", "studio.png", None);

        var package = attached.Package.ShouldNotBeNull();
        (attached.Status, attached.BackgroundFile, package.Status, package.RightsConfirmed, package.ApprovedAt)
            .ShouldBe(("media_ready", "studio.png", "assembled", false, (DateTimeOffset?)null));
    }

    [Fact]
    public async Task AttachBackground_RefusesAFileNotInTheMediaFolder()
    {
        var project = await ScriptReadyAsync();

        var ex = await Should.ThrowAsync<OperatorValidationException>(() =>
            _operator.AttachSceneMediaAsync(project.Id, string.Empty, "background", "missing.png", None));

        ex.Message.ShouldBe("'missing.png' is not in the project's media folder; copy it there first.");
        (await StoredAsync(project.Id)).BackgroundFile.ShouldBeNull();
    }

    [Fact]
    public async Task AttachBackground_RefusesASceneId()
    {
        var project = await WithScenesAsync(Scene("s1", 1));

        var ex = await Should.ThrowAsync<OperatorValidationException>(() =>
            _operator.AttachSceneMediaAsync(project.Id, "s1", "background", "studio.png", None));

        ex.Message.ShouldBe("A background is attached to the project, not to a scene.");
        var stored = await StoredAsync(project.Id);
        stored.BackgroundFile.ShouldBeNull();
        stored.Scenes.ShouldBe(project.Scenes);
    }

    [Theory]
    [InlineData("../studio.png")]
    [InlineData("a/studio.png")]
    [InlineData(".studio.png")]
    [InlineData("studio.mp3")]
    [InlineData("studio.gif")]
    public async Task AttachBackground_RefusesANameThatIsNotAPlainPicture(string fileName)
    {
        var project = await ScriptReadyAsync();

        var ex = await Should.ThrowAsync<OperatorValidationException>(() =>
            _operator.AttachSceneMediaAsync(project.Id, string.Empty, "background", fileName, None));

        ex.Message.ShouldBe($"'{fileName}' is not a visual file Scout accepts: a plain file name of at most 120 characters, without a folder or a leading dot, ending in .png, .jpg, .jpeg, .webp.");
        (await StoredAsync(project.Id)).BackgroundFile.ShouldBeNull();
    }

    [Fact]
    public async Task AttachBackground_NeedsNoYouTubeWriteGrant()
    {
        var project = await ScriptReadyAsync();
        await PutInMediaAsync(project.Id, "studio.png");
        await RevokeYouTubeAsync();

        var attached = await _operator.AttachSceneMediaAsync(project.Id, string.Empty, "background", "studio.png", None);

        attached.BackgroundFile.ShouldBe("studio.png");
        (await StoredAsync(project.Id)).BackgroundFile.ShouldBe("studio.png");
    }

    [Fact]
    public async Task WorkerSave_KeepsTheOwnersBackground()
    {
        var project = await ScriptReadyAsync();
        await PutInMediaAsync(project.Id, "studio.png");
        await _operator.AttachSceneMediaAsync(project.Id, string.Empty, "background", "studio.png", None);
        await _operator.ProduceProjectAsync(project.Id, string.Empty, string.Empty, string.Empty, None);
        var taken = (await _operator.NextQueuedProjectAsync(None)).ShouldNotBeNull();
        taken.BackgroundFile.ShouldBe("studio.png");

        var dropped = await _operator.SaveProjectFromWorkerAsync(taken with { BackgroundFile = null, Scenes = [Scene("s1", 1)] }, null, None);
        var replaced = await _operator.SaveProjectFromWorkerAsync(
            dropped with { Status = "media_ready", BackgroundFile = "other.png" }, null, None);

        dropped.BackgroundFile.ShouldBe("studio.png");
        replaced.BackgroundFile.ShouldBe("studio.png");
        (await StoredAsync(project.Id)).BackgroundFile.ShouldBe("studio.png");
    }

    [Fact]
    public async Task A_Worker_Save_Keeps_The_Owners_Scene_Text()
    {
        var project = await ProducingWithScenesAsync(Scene("s1", 1), Scene("s2", 2));
        await _operator.EditSceneTextAsync(project.Id, "s1", "The owner's words.", None);
        var taken = (await _operator.NextQueuedProjectAsync(None)).ShouldNotBeNull();

        var saved = await _operator.SaveProjectFromWorkerAsync(
            taken with { Scenes = [.. taken.Scenes.Select(s => s with { Narration = $"The worker's words for {s.Id}.", Status = "ready" })] }, null, None);

        saved.Scenes.Select(s => (s.Id, s.Narration, s.Status)).ShouldBe([("s1", "The owner's words.", "ready"), ("s2", "Narration s2", "ready")]);
        (await StoredAsync(project.Id)).Scenes.Select(s => s.Narration).ShouldBe(["The owner's words.", "Narration s2"]);
    }

    [Fact]
    public async Task The_Media_Directory_Is_The_Projects_Folder_Under_Operator_Beside_Features_Json_Created_On_Demand()
    {
        var project = await ScriptReadyAsync();
        var expected = Path.Combine(_directory, "operator", project.Id);
        Directory.Exists(expected).ShouldBeFalse();

        var path = await _operator.GetMediaDirectoryAsync(project.Id, CancellationToken.None);
        var forWorker = await _operator.GetMediaDirectoryForWorkerAsync(project.Id, CancellationToken.None);

        (path, forWorker).ShouldBe((expected, expected));
        Path.IsPathRooted(path).ShouldBeTrue();
        Directory.Exists(expected).ShouldBeTrue();
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("..")]
    [InlineData("/tmp")]
    [InlineData("p1")]
    public async Task A_Media_Directory_Is_Given_Only_For_A_Project_Id_So_No_Path_Escapes(string id)
    {
        await _store.UpdateProjectsAsync(
            projects => { projects.Add(new OperatorProject(id, "Seeded topic", "script_ready", "desktop", default, default, [], null, [])); return 0; },
            CancellationToken.None);
        var before = Directory.GetFileSystemEntries(_directory, "*", SearchOption.AllDirectories).Order().ToList();

        var ex = await Should.ThrowAsync<OperatorValidationException>(() => _operator.GetMediaDirectoryAsync(id, CancellationToken.None));

        ex.Message.ShouldBe($"'{id}' is not a project id.");
        Directory.GetFileSystemEntries(_directory, "*", SearchOption.AllDirectories).Order().ShouldBe(before);
        Directory.Exists(Path.Combine(_directory, "escape")).ShouldBeFalse();
    }

    [Fact]
    public async Task The_Media_Directory_Of_An_Unknown_Project_Is_Not_Found_And_Not_Created()
    {
        var id = Guid.NewGuid().ToString("N");

        var ex = await Should.ThrowAsync<OperatorNotFoundException>(() => _operator.GetMediaDirectoryAsync(id, CancellationToken.None));

        ex.Message.ShouldBe($"Unknown project '{id}'.");
        Directory.Exists(Path.Combine(_directory, "operator", id)).ShouldBeFalse();
    }

    [Fact]
    public async Task Without_The_Owners_Grant_The_Worker_Gets_No_Media_Directory()
    {
        var project = await ScriptReadyAsync();
        await RevokeYouTubeAsync();

        var ex = await Should.ThrowAsync<OperatorWriteRefusedException>(() =>
            _operator.GetMediaDirectoryForWorkerAsync(project.Id, CancellationToken.None));

        ex.Message.ShouldBe(NotAllowed);
        Directory.Exists(Path.Combine(_directory, "operator", project.Id)).ShouldBeFalse();
    }

    [Fact]
    public async Task The_Worker_Adds_Scenes_To_A_Project_Without_Any_And_Then_Sets_What_Is_Its_To_Set()
    {
        var project = await ProducingWithScenesAsync(Scene("s1", 1, status: "planned"), Scene("s2", 2, status: "planned"));
        var generated = project.Scenes[0] with
        {
            Status = "ready",
            VisualPrompt = "A meander from above",
            AudioFile = "s1-v2.wav",
            VisualFile = "s1-v2.jpg",
            DurationMs = 5100,
        };
        var failed = project.Scenes[1] with { Status = "failed", Error = "No image service.", VisualSource = "text_card", AudioSource = "none", AudioFile = null };

        var saved = await _operator.SaveProjectFromWorkerAsync(project with { Scenes = [generated, failed] }, null, CancellationToken.None);

        project.Scenes.Select(s => (s.Id, s.Order, s.Status)).ShouldBe([("s1", 1, "planned"), ("s2", 2, "planned")]);
        saved.Scenes.ShouldBe([generated, failed]);
        (await StoredAsync(project.Id)).Scenes.ShouldBe([generated, failed]);
    }

    [Fact]
    public async Task A_Project_The_Worker_Starts_May_Carry_Its_Scenes()
    {
        await GrantYouTubeAsync();

        var created = await _operator.SaveProjectFromWorkerAsync(
            NewDraft("Glaciers explained", "producing") with { Scenes = [Scene("s1", 1, status: "planned")] },
            null,
            CancellationToken.None);

        created.Scenes.ShouldHaveSingleItem().Id.ShouldBe("s1");
    }

    [Theory]
    [InlineData("locked", "Only the owner locks a scene; scene 's1' was not.")]
    [InlineData("owner-audio", "Only the owner attaches their own audio; scene 's1' has none.")]
    [InlineData("owner-visual", "Only the owner attaches their own visual; scene 's1' has none.")]
    public async Task The_Worker_Cannot_Add_A_Locked_Scene_Or_One_With_Owner_Media(string change, string message)
    {
        var project = await ScriptReadyAsync();
        var scene = change switch
        {
            "locked" => Scene("s1", 1, locked: true),
            "owner-audio" => Scene("s1", 1, audioSource: "owner"),
            _ => Scene("s1", 1, visualSource: "owner"),
        };

        var onExisting = await Should.ThrowAsync<OperatorTamperingException>(() =>
            _operator.SaveProjectFromWorkerAsync(project with { Scenes = [scene] }, null, CancellationToken.None));
        var onNew = await Should.ThrowAsync<OperatorTamperingException>(() =>
            _operator.SaveProjectFromWorkerAsync(NewDraft("Glaciers explained", "producing") with { Scenes = [scene] }, null, CancellationToken.None));

        (onExisting.Message, onNew.Message).ShouldBe((message, message));
        (await StoredAsync(project.Id)).Scenes.ShouldBeEmpty();
        (await _operator.ListProjectsAsync(0, CancellationToken.None)).ShouldHaveSingleItem();
    }

    [Theory]
    [InlineData("add")]
    [InlineData("drop")]
    [InlineData("swap")]
    [InlineData("none")]
    public async Task Once_A_Project_Has_Scenes_The_Worker_Cannot_Add_Or_Drop_One(string change)
    {
        var project = await ProducingWithScenesAsync(Scene("s1", 1), Scene("s2", 2));
        IReadOnlyList<Scene> scenes = change switch
        {
            "add" => [.. project.Scenes, Scene("s3", 3)],
            "drop" => [project.Scenes[0]],
            "swap" => [project.Scenes[0], Scene("s3", 2)],
            _ => [],
        };

        var ex = await Should.ThrowAsync<OperatorTamperingException>(() =>
            _operator.SaveProjectFromWorkerAsync(project with { Scenes = scenes }, null, CancellationToken.None));

        ex.Message.ShouldBe($"Project '{project.Id}' has its scenes; the worker may not add or drop one.");
        (await StoredAsync(project.Id)).Scenes.Select(s => s.Id).ShouldBe(["s1", "s2"]);
    }

    [Theory]
    [InlineData("order")]
    [InlineData("lock")]
    public async Task The_Workers_Order_And_Lock_Of_A_Scene_Are_Ignored_And_The_Owners_Kept(string change)
    {
        var project = await ProducingWithScenesAsync(Scene("s1", 1), Scene("s2", 2));
        var changed = change == "order" ? project.Scenes[1] with { Order = 1 } : project.Scenes[1] with { Locked = true };

        var saved = await _operator.SaveProjectFromWorkerAsync(
            project with { Scenes = [project.Scenes[0], changed with { VisualPrompt = "A new prompt." }] }, null, CancellationToken.None);

        saved.Scenes.ShouldBe([project.Scenes[0], project.Scenes[1] with { VisualPrompt = "A new prompt." }]);
        (await StoredAsync(project.Id)).Scenes.ShouldBe(saved.Scenes);
    }

    [Theory]
    [InlineData("narration")]
    [InlineData("status")]
    [InlineData("duration")]
    [InlineData("unlock")]
    public async Task A_Locked_Scene_Is_Kept_Exactly_As_Stored_Whatever_The_Worker_Sends(string change)
    {
        var project = await ProducingWithScenesAsync(Scene("s1", 1));
        var locked = (await _operator.SetSceneLockedAsync(project.Id, "s1", true, CancellationToken.None)).Scenes[0];
        var changed = change switch
        {
            "narration" => locked with { Narration = "Rewritten." },
            "status" => locked with { Status = "planned" },
            "duration" => locked with { DurationMs = 1 },
            _ => locked with { Locked = false },
        };

        var saved = await _operator.SaveProjectFromWorkerAsync(project with { Scenes = [changed] }, null, CancellationToken.None);

        saved.Scenes.ShouldBe([locked]);
        (await StoredAsync(project.Id)).Scenes.ShouldBe([locked]);
    }

    [Fact]
    public async Task The_Worker_Sends_A_Locked_Scene_Back_Unchanged_And_Saves_The_Others()
    {
        var project = await ProducingWithScenesAsync(Scene("s1", 1), Scene("s2", 2));
        var locked = (await _operator.SetSceneLockedAsync(project.Id, "s1", true, CancellationToken.None)).Scenes[0];
        var regenerated = project.Scenes[1] with { VisualPrompt = "A new prompt." };

        var saved = await _operator.SaveProjectFromWorkerAsync(project with { Scenes = [locked, regenerated] }, null, CancellationToken.None);

        saved.Scenes.ShouldBe([locked, regenerated]);
    }

    [Theory]
    [InlineData("audio", "generated", "mine.mp3")]
    [InlineData("audio", "owner", "other.mp3")]
    [InlineData("audio", "none", null)]
    [InlineData("visual", "text_card", "mine.png")]
    [InlineData("visual", "owner", "other.png")]
    public async Task The_Owners_Media_Is_Kept_Whatever_The_Worker_Sends_In_Its_Place(string kind, string source, string? file)
    {
        var project = await ProducingWithScenesAsync(Scene("s1", 1));
        var media = await _operator.GetMediaDirectoryAsync(project.Id, CancellationToken.None);
        var ownFile = kind == "audio" ? "mine.mp3" : "mine.png";
        await File.WriteAllTextAsync(Path.Combine(media, ownFile), "media");
        var attached = (await _operator.AttachSceneMediaAsync(project.Id, "s1", kind, ownFile, CancellationToken.None)).Scenes[0];
        var changed = kind == "audio"
            ? attached with { AudioSource = source, AudioFile = file }
            : attached with { VisualSource = source, VisualFile = file };

        var saved = await _operator.SaveProjectFromWorkerAsync(project with { Scenes = [changed] }, null, CancellationToken.None);

        saved.Scenes.ShouldBe([attached]);
        (await StoredAsync(project.Id)).Scenes.ShouldBe([attached]);
    }

    [Fact]
    public async Task The_Worker_Keeps_The_Owners_Media_And_Sets_The_Duration_Of_That_Scene()
    {
        var project = await ProducingWithScenesAsync(Scene("s1", 1));
        var media = await _operator.GetMediaDirectoryAsync(project.Id, CancellationToken.None);
        await File.WriteAllTextAsync(Path.Combine(media, "mine.mp3"), "audio");
        var attached = (await _operator.AttachSceneMediaAsync(project.Id, "s1", "audio", "mine.mp3", CancellationToken.None)).Scenes[0];

        var saved = await _operator.SaveProjectFromWorkerAsync(
            project with { Scenes = [attached with { DurationMs = 7300, VisualFile = "s1-v2.png" }] }, null, CancellationToken.None);

        (saved.Scenes[0].AudioSource, saved.Scenes[0].AudioFile, saved.Scenes[0].DurationMs, saved.Scenes[0].VisualFile)
            .ShouldBe(("owner", (string?)"mine.mp3", 7300, (string?)"s1-v2.png"));
    }

    [Theory]
    [InlineData("", "Every scene needs an id.")]
    [InlineData("dup", "Scene ids must be unique within a project.")]
    [InlineData("status", "Scene 's1' has a status, source or duration the operator does not accept.")]
    [InlineData("audio-source", "Scene 's1' has a status, source or duration the operator does not accept.")]
    [InlineData("visual-source", "Scene 's1' has a status, source or duration the operator does not accept.")]
    [InlineData("duration", "Scene 's1' has a status, source or duration the operator does not accept.")]
    [InlineData("audio-file", "'../s1.mp3' is not an audio file Scout accepts: a plain file name of at most 120 characters, without a folder or a leading dot, ending in .mp3, .wav, .m4a, .ogg.")]
    [InlineData("visual-file", "'img/s1.png' is not a visual file Scout accepts: a plain file name of at most 120 characters, without a folder or a leading dot, ending in .png, .jpg, .jpeg, .webp.")]
    public async Task A_Worker_Scene_Outside_The_Vocabulary_Or_With_An_Unsafe_File_Is_Refused(string change, string message)
    {
        var project = await ScriptReadyAsync();
        IReadOnlyList<Scene> scenes = change switch
        {
            "" => [Scene(" ", 1)],
            "dup" => [Scene("s1", 1), Scene("s1", 2)],
            "status" => [Scene("s1", 1, status: "done")],
            "audio-source" => [Scene("s1", 1, audioSource: "tts")],
            "visual-source" => [Scene("s1", 1, visualSource: "video")],
            "duration" => [Scene("s1", 1) with { DurationMs = -1 }],
            "audio-file" => [Scene("s1", 1, audioFile: "../s1.mp3")],
            _ => [Scene("s1", 1, visualFile: "img/s1.png")],
        };

        var ex = await Should.ThrowAsync<OperatorValidationException>(() =>
            _operator.SaveProjectFromWorkerAsync(project with { Scenes = scenes }, null, CancellationToken.None));

        ex.Message.ShouldBe(message);
        (await StoredAsync(project.Id)).Scenes.ShouldBeEmpty();
    }

    [Fact]
    public async Task Before_The_Worker_Reports_The_Readiness_Is_Empty_And_Says_So()
    {
        var readiness = await _operator.GetReadinessAsync(CancellationToken.None);

        (readiness.SpeechAvailable, readiness.ImagesAvailable, readiness.Provider, readiness.FfmpegAvailable, readiness.YouTubeSignedIn, readiness.ReportedAt)
            .ShouldBe((false, false, string.Empty, false, false, (DateTimeOffset?)null));
        readiness.Notes.ShouldBe(["The Scout worker has not reported yet."]);
        readiness.Voices.ShouldBeEmpty();
    }

    [Fact]
    public async Task The_Latest_Readiness_Report_Is_Kept_With_The_Time_The_Control_Plane_Received_It()
    {
        var before = DateTimeOffset.UtcNow;

        await _operator.ReportReadinessAsync(new Readiness(false, false, "gemini", false, false, ["old"], DateTimeOffset.UnixEpoch), CancellationToken.None);
        var reported = await _operator.ReportReadinessAsync(
            new Readiness(true, true, "openai", true, true, ["Images come from dall-e-3."], DateTimeOffset.UnixEpoch),
            CancellationToken.None);
        var read = await _operator.GetReadinessAsync(CancellationToken.None);

        (read.SpeechAvailable, read.ImagesAvailable, read.Provider, read.FfmpegAvailable, read.YouTubeSignedIn).ShouldBe((true, true, "openai", true, true));
        read.Notes.ShouldBe(["Images come from dall-e-3."]);
        read.ReportedAt.ShouldNotBeNull().ShouldBeInRange(before, DateTimeOffset.UtcNow);
        read.ReportedAt.ShouldBe(reported.ReportedAt);
    }

    [Fact]
    public async Task The_Readiness_Keeps_The_Voices_The_Worker_Offers()
    {
        Voice[] voices = [new("OpenAI:nova", "nova (OpenAI)", "OpenAI"), new("piper:en_GB-cori-medium", "Cori (medium)", "piper")];

        await _operator.ReportReadinessAsync(new Readiness(true, true, "OpenAI", true, true, []) { Voices = voices }, CancellationToken.None);

        (await _operator.GetReadinessAsync(CancellationToken.None)).Voices.ShouldBe(voices);
    }

    [Fact]
    public async Task When_Readiness_Json_Cannot_Be_Written_The_Report_Fails_Saying_So_And_The_Earlier_Report_Stays_Readable()
    {
        var earlier = await _operator.ReportReadinessAsync(
            new Readiness(true, false, "openai", true, false, ["No image model is configured."]), CancellationToken.None);
        Directory.CreateDirectory(_store.ReadinessPath + ".tmp");

        var ex = await Should.ThrowAsync<OperatorStoreException>(() => _operator.ReportReadinessAsync(
            new Readiness(true, true, "openai", true, true, []), CancellationToken.None));

        ex.Message.ShouldBe("Scout could not save the worker's readiness report in its data folder.");
        var read = await _operator.GetReadinessAsync(CancellationToken.None);
        (read.ImagesAvailable, read.Notes.Single(), read.ReportedAt).ShouldBe((false, "No image model is configured.", earlier.ReportedAt));
    }

    [Fact]
    public async Task A_Malformed_Readiness_File_Is_Moved_Aside_And_The_Desktop_Reads_That_The_Worker_Has_Not_Reported()
    {
        await File.WriteAllTextAsync(_store.ReadinessPath, "{ not json");

        var readiness = await _operator.GetReadinessAsync(CancellationToken.None);

        (readiness.ReportedAt, readiness.Notes.Single()).ShouldBe(((DateTimeOffset?)null, "The Scout worker has not reported yet."));
        File.Exists(_store.ReadinessPath).ShouldBeFalse();
        Directory.GetFiles(_directory, "readiness.json.corrupt-*").ShouldHaveSingleItem();
    }

    [Fact]
    public async Task When_The_Media_Folder_Cannot_Be_Created_The_Call_Fails_And_The_Projects_Stay_As_They_Were()
    {
        var project = await ScriptReadyAsync();
        await File.WriteAllTextAsync(_store.MediaRoot, "a file where the folder belongs");
        var bytes = await File.ReadAllBytesAsync(_store.ProjectsPath);

        await Should.ThrowAsync<IOException>(() => _operator.GetMediaDirectoryAsync(project.Id, CancellationToken.None));

        (await File.ReadAllBytesAsync(_store.ProjectsPath)).ShouldBe(bytes);
        File.ReadAllText(_store.MediaRoot).ShouldBe("a file where the folder belongs");
    }

    private static readonly CancellationToken None = CancellationToken.None;

    private static VideoPackage? Normal(VideoPackage? package) =>
        package is null ? null : package with { Shorts = [.. package.Shorts], Checks = [.. package.Checks], Tags = [.. package.Tags] };

    private async Task<OperatorProject> AssemblingAsync()
    {
        var project = await WithScenesAsync(Scene("s1", 1), Scene("s2", 2));
        await _operator.AssembleProjectAsync(project.Id, None);
        var taken = (await _operator.NextQueuedProjectAsync(None)).ShouldNotBeNull();
        taken.Status.ShouldBe("assembling");
        var media = await _operator.GetMediaDirectoryAsync(project.Id, None);
        foreach (var name in new[] { "video.mp4", "short-1.mp4", "thumb.png" })
        {
            await File.WriteAllTextAsync(Path.Combine(media, name), "media");
        }
        return taken;
    }

    private static VideoPackage Rendered(VideoPackage queued) => queued with
    {
        Status = "assembled",
        VideoFile = "video.mp4",
        Shorts = [new ShortClip("short-1.mp4", 0, 30000, "Rivers never run straight.")],
        ThumbnailFile = "thumb.png",
        Checks = [new PackageCheck("duration", "pass", "6 seconds"), new PackageCheck("loudness", "warn", "-12 LUFS")],
        Title = "Why rivers bend",
        Description = "Meanders explained.",
        Tags = ["rivers", "geography"],
        DurationMs = 6000,
        AssembledAt = new DateTimeOffset(2026, 11, 14, 14, 12, 0, TimeSpan.Zero),
        ThumbnailDescription = "A frame from the video at a tenth of its length.",
    };

    private async Task<OperatorProject> AssembledAsync(Func<VideoPackage, VideoPackage>? change = null)
    {
        var taken = await AssemblingAsync();
        var package = Rendered(taken.Package.ShouldNotBeNull());
        return await _operator.SaveProjectFromWorkerAsync(
            taken with { Status = "assembled", Package = change is null ? package : change(package) }, null, None);
    }

    private async Task<OperatorProject> ReadyToApproveAsync(Func<VideoPackage, VideoPackage>? change = null)
    {
        var project = await AssembledAsync(change);
        return await _operator.ConfirmMediaRightsAsync(project.Id, None);
    }

    private async Task PutInMediaAsync(string projectId, string fileName)
    {
        var media = await _operator.GetMediaDirectoryAsync(projectId, None);
        await File.WriteAllTextAsync(Path.Combine(media, fileName), "image");
    }

    private Task<int> SetStoredAsync(string id, Func<OperatorProject, OperatorProject> change) =>
        _store.UpdateProjectsAsync(
            projects =>
            {
                var index = projects.FindIndex(p => p.Id == id);
                projects[index] = change(projects[index]);
                return 0;
            },
            None);

    [Fact]
    public async Task Next_Takes_A_Project_Queued_For_Assembly_After_Production_And_Before_Stale_Ones_And_A_Stale_Assembly_Again()
    {
        await GrantYouTubeAsync();
        var now = DateTimeOffset.UtcNow;
        var queuedPackage = PackageRules.Queue(null);
        OperatorProject Stored(string id, string status, TimeSpan idle, VideoPackage? package = null) =>
            new(id, $"Topic {id}", status, "desktop", now.AddHours(-3), now - idle, [], null, []) { Package = package };
        await _store.UpdateProjectsAsync(
            projects =>
            {
                projects.Add(Stored("stuck-assembling", "assembling", TimeSpan.FromMinutes(30), queuedPackage with { Status = "assembling" }));
                projects.Add(Stored("live-assembling", "assembling", TimeSpan.FromMinutes(2), queuedPackage with { Status = "assembling" }));
                projects.Add(Stored("assemble-me", "assembly_queued", TimeSpan.FromMinutes(1), queuedPackage));
                projects.Add(Stored("produce-me", "production_queued", TimeSpan.FromMinutes(1)));
                projects.Add(Stored("queued", "queued", TimeSpan.FromMinutes(1)));
                return 0;
            },
            None);

        var taken = new List<OperatorProject?>();
        for (var i = 0; i < 5; i++)
        {
            taken.Add(await _operator.NextQueuedProjectAsync(None));
        }

        taken.Select(p => (p?.Id, p?.Status, p?.Package?.Status)).ShouldBe([
            ("queued", "researching", null),
            ("produce-me", "producing", null),
            ("assemble-me", "assembling", "assembling"),
            ("stuck-assembling", "assembling", "assembling"),
            (null, null, null),
        ]);
        (await StoredAsync("live-assembling")).UpdatedAt.ShouldBe(now - TimeSpan.FromMinutes(2));
    }

    [Fact]
    public async Task Assembling_A_Media_Ready_Project_With_Every_Scene_Ready_Queues_It_With_An_Empty_Package()
    {
        var project = await WithScenesAsync(Scene("s1", 1), Scene("s2", 2));

        var queued = await _operator.AssembleProjectAsync(project.Id, None);

        queued.Status.ShouldBe("assembly_queued");
        Normal(queued.Package).ShouldBeEquivalentTo(Normal(new VideoPackage(
            "assembly_queued", null, [], null, [], false, null, string.Empty, string.Empty, [], 0, null, false)));
        (await StoredAsync(project.Id)).Status.ShouldBe("assembly_queued");
    }

    [Fact]
    public async Task Assembling_A_Failed_Assembly_Again_Clears_The_Project_And_Package_Errors()
    {
        var taken = await AssemblingAsync();
        await _operator.SaveProjectFromWorkerAsync(
            taken with { Status = "failed", Error = "Assembly failed.", Package = taken.Package! with { Status = "failed", Error = "ffmpeg exited with 1." } },
            null,
            None);

        var queued = await _operator.AssembleProjectAsync(taken.Id, None);

        (queued.Status, queued.Error, queued.Package!.Status, queued.Package.Error)
            .ShouldBe(("assembly_queued", (string?)null, "assembly_queued", (string?)null));
    }

    [Theory]
    [InlineData("script_ready")]
    [InlineData("producing")]
    [InlineData("assembly_queued")]
    [InlineData("assembling")]
    [InlineData("approved")]
    public async Task Only_A_Media_Ready_Assembled_Or_Failed_Project_Is_Assembled(string status)
    {
        var project = await WithScenesAsync(Scene("s1", 1));
        await SetStoredAsync(project.Id, p => p with { Status = status });

        var ex = await Should.ThrowAsync<OperatorValidationException>(() => _operator.AssembleProjectAsync(project.Id, None));

        ex.Message.ShouldBe($"Only a project with its media ready, an assembled one, or a failed one with every scene ready is assembled; this one is {status}.");
        (await StoredAsync(project.Id)).Status.ShouldBe(status);
    }

    [Theory]
    [InlineData("media_ready", 1, "1 scene is not ready; regenerate it or attach its media before assembly.")]
    [InlineData("failed", 1, "1 scene is not ready; regenerate it or attach its media before assembly.")]
    [InlineData("failed", 2, "2 scenes are not ready; regenerate them or attach their media before assembly.")]
    [InlineData("media_ready", 0, "The project has no scenes to assemble.")]
    public async Task A_Project_With_A_Scene_Not_Ready_Or_No_Scenes_Is_Not_Assembled(string status, int notReady, string message)
    {
        var project = await WithScenesAsync(Scene("s1", 1), Scene("s2", 2), Scene("s3", 3));
        await SetStoredAsync(project.Id, p => p with
        {
            Status = status,
            Scenes = notReady == 0 ? [] : [.. p.Scenes.Select((s, i) => i < notReady ? s with { Status = "failed" } : s)],
        });

        var ex = await Should.ThrowAsync<OperatorValidationException>(() => _operator.AssembleProjectAsync(project.Id, None));

        ex.Message.ShouldBe(message);
        (await StoredAsync(project.Id)).Status.ShouldBe(status);
    }

    [Fact]
    public async Task A_Failed_Project_With_Every_Scene_Ready_Is_Assembled()
    {
        var project = await WithScenesAsync(Scene("s1", 1));
        await SetStoredAsync(project.Id, p => p with { Status = "failed", Error = "Speech timed out." });

        var queued = await _operator.AssembleProjectAsync(project.Id, None);

        (queued.Status, queued.Error).ShouldBe(("assembly_queued", (string?)null));
    }

    [Fact]
    public async Task Without_The_Owners_Grant_A_Project_Is_Not_Assembled_And_A_Package_Not_Approved()
    {
        var project = await WithScenesAsync(Scene("s1", 1));
        var ready = await ReadyToApproveAsync();
        await RevokeYouTubeAsync();

        var assemble = await Should.ThrowAsync<OperatorWriteRefusedException>(() => _operator.AssembleProjectAsync(project.Id, None));
        var approve = await Should.ThrowAsync<OperatorWriteRefusedException>(() => _operator.ApprovePackageAsync(ready.Id, None));

        (assemble.Message, approve.Message).ShouldBe((NotAllowed, NotAllowed));
        ((await StoredAsync(project.Id)).Status, (await StoredAsync(ready.Id)).Status).ShouldBe(("media_ready", "assembled"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Assembling_Again_Clears_The_Rights_And_The_Approval_And_Keeps_The_Metadata_Only_When_The_Owner_Edited_It(bool edited)
    {
        var project = await ReadyToApproveAsync();
        DateTimeOffset? savedAt = null;
        if (edited)
        {
            savedAt = (await _operator.EditPackageMetadataAsync(project.Id, "My title", "My description", ["mine"], None)).Package!.MetadataSavedAt;
            await _operator.ConfirmMediaRightsAsync(project.Id, None);
        }
        await _operator.ApprovePackageAsync(project.Id, None);
        await SetStoredAsync(project.Id, p => p with { Status = "assembled" });

        var package = (await _operator.AssembleProjectAsync(project.Id, None)).Package.ShouldNotBeNull();

        (package.RightsConfirmed, package.ApprovedAt, package.VideoFile, package.Shorts.Count, package.Checks.Count)
            .ShouldBe((false, (DateTimeOffset?)null, (string?)null, 0, 0));
        (package.Title, package.Description, string.Join("|", package.Tags), package.MetadataEdited).ShouldBe(edited
            ? ("My title", "My description", "mine", true)
            : (string.Empty, string.Empty, string.Empty, false));
        (package.RightsConfirmedAt, package.AssembledAt, package.ThumbnailDescription).ShouldBe(
            ((DateTimeOffset?)null, (DateTimeOffset?)null, (string?)null), "a new assembly starts with no rights confirmation time, assembly time or thumbnail description");
        package.MetadataSavedAt.ShouldBe(savedAt, "when the owner saved the metadata is kept with the metadata, only when the owner edited it");
    }

    [Fact]
    public async Task The_Worker_Reports_An_Assembled_Package_Whose_Files_Are_In_The_Media_Folder()
    {
        var taken = await AssemblingAsync();
        var package = Rendered(taken.Package!);

        var saved = await _operator.SaveProjectFromWorkerAsync(taken with { Status = "assembled", Package = package }, null, None);

        saved.Status.ShouldBe("assembled");
        Normal(saved.Package).ShouldBeEquivalentTo(Normal(package));
        Normal((await StoredAsync(taken.Id)).Package).ShouldBeEquivalentTo(Normal(package));
        (await _writes.ListAsync(1, None)).Single().Outcome.ShouldBe("saved assembled");
    }

    [Theory]
    [InlineData("video", "'video.mp4' is not in the project's media folder.")]
    [InlineData("short", "'short-1.mp4' is not in the project's media folder.")]
    [InlineData("thumbnail", "'thumb.png' is not in the project's media folder.")]
    [InlineData("no-video", "An assembled package needs its video file.")]
    public async Task An_Assembled_Report_Naming_A_File_Not_In_The_Media_Folder_Or_Without_A_Video_Is_Refused(string missing, string message)
    {
        var taken = await AssemblingAsync();
        var media = await _operator.GetMediaDirectoryAsync(taken.Id, None);
        var package = Rendered(taken.Package!);
        switch (missing)
        {
            case "video": File.Delete(Path.Combine(media, "video.mp4")); break;
            case "short": File.Delete(Path.Combine(media, "short-1.mp4")); break;
            case "thumbnail": File.Delete(Path.Combine(media, "thumb.png")); break;
            default: package = package with { VideoFile = null }; break;
        }

        var ex = await Should.ThrowAsync<OperatorValidationException>(() =>
            _operator.SaveProjectFromWorkerAsync(taken with { Status = "assembled", Package = package }, null, None));

        ex.Message.ShouldBe(message);
        (await StoredAsync(taken.Id)).Status.ShouldBe("assembling");
    }

    [Theory]
    [InlineData("video", "../video.mp4", "'../video.mp4' is not a video file Scout accepts")]
    [InlineData("video", "video.mov", "'video.mov' is not a video file Scout accepts")]
    [InlineData("video", ".video.mp4", "'.video.mp4' is not a video file Scout accepts")]
    [InlineData("short", "short/1.mp4", "'short/1.mp4' is not a video file Scout accepts")]
    [InlineData("short", "short-1.png", "'short-1.png' is not a video file Scout accepts")]
    [InlineData("thumbnail", "thumb.webp", "'thumb.webp' is not a thumbnail file Scout accepts")]
    [InlineData("thumbnail", "..\\thumb.png", "'..\\thumb.png' is not a thumbnail file Scout accepts")]
    public async Task An_Unsafe_Or_Foreign_Package_File_Name_Is_Refused(string kind, string name, string message)
    {
        var taken = await AssemblingAsync();
        var package = Rendered(taken.Package!) with { Status = "assembling" };
        package = kind switch
        {
            "video" => package with { VideoFile = name },
            "short" => package with { Shorts = [new ShortClip(name, 0, 1000, "Hook")] },
            _ => package with { ThumbnailFile = name },
        };

        var ex = await Should.ThrowAsync<OperatorValidationException>(() =>
            _operator.SaveProjectFromWorkerAsync(taken with { Package = package }, null, None));

        ex.Message.ShouldStartWith(message);
        (await StoredAsync(taken.Id)).Package!.VideoFile.ShouldBeNull();
    }

    [Theory]
    [InlineData("check", "Check 'duration' has a result the operator does not accept: pass, warn, fail.")]
    [InlineData("unnamed-check", "Every check needs a name.")]
    [InlineData("duration", "A package or Shorts duration, or a Shorts start, is below 0.")]
    [InlineData("short-start", "A package or Shorts duration, or a Shorts start, is below 0.")]
    [InlineData("status", "Package status 'published' is not one the operator accepts: assembly_queued, assembling, assembled, approved, failed.")]
    public async Task A_Package_Outside_The_Vocabulary_Is_Refused(string change, string message)
    {
        var taken = await AssemblingAsync();
        var package = Rendered(taken.Package!) with { Status = "assembling" };
        package = change switch
        {
            "check" => package with { Checks = [new PackageCheck("duration", "maybe", string.Empty)] },
            "unnamed-check" => package with { Checks = [new PackageCheck(" ", "pass", string.Empty)] },
            "duration" => package with { DurationMs = -1 },
            "short-start" => package with { Shorts = [new ShortClip("short-1.mp4", -1, 1000, "Hook")] },
            _ => package with { Status = "published" },
        };

        var ex = await Should.ThrowAsync<OperatorValidationException>(() =>
            _operator.SaveProjectFromWorkerAsync(taken with { Package = package }, null, None));

        ex.Message.ShouldBe(message);
    }

    [Theory]
    [InlineData("rights", "Only the owner confirms the media rights of project '{0}'.")]
    [InlineData("approved-at", "Only the owner approves the package of project '{0}'.")]
    [InlineData("approved", "Only the owner sets the package of project '{0}' to approved.")]
    [InlineData("assembly_queued", "Only the owner sets the package of project '{0}' to assembly_queued.")]
    [InlineData("edited", "Only the owner edits the package metadata of project '{0}'.")]
    public async Task The_Worker_Cannot_Confirm_Rights_Approve_Queue_Or_Mark_The_Metadata_Edited(string change, string message)
    {
        var taken = await AssemblingAsync();
        var package = Rendered(taken.Package!);
        package = change switch
        {
            "rights" => package with { RightsConfirmed = true },
            "approved-at" => package with { ApprovedAt = DateTimeOffset.UtcNow },
            "edited" => package with { MetadataEdited = true },
            _ => package with { Status = change },
        };

        var ex = await Should.ThrowAsync<OperatorTamperingException>(() =>
            _operator.SaveProjectFromWorkerAsync(taken with { Status = "assembled", Package = package }, null, None));

        ex.Message.ShouldBe(string.Format(System.Globalization.CultureInfo.InvariantCulture, message, taken.Id));
        var stored = (await StoredAsync(taken.Id)).Package.ShouldNotBeNull();
        (stored.Status, stored.RightsConfirmed, stored.ApprovedAt, stored.MetadataEdited)
            .ShouldBe(("assembling", false, (DateTimeOffset?)null, false));
    }

    [Theory]
    [InlineData("title")]
    [InlineData("description")]
    [InlineData("tags")]
    public async Task Once_The_Owner_Edited_The_Metadata_The_Worker_Cannot_Change_It(string field)
    {
        var project = await AssembledAsync();
        await _operator.EditPackageMetadataAsync(project.Id, "My title", "My description", ["mine"], None);
        await _operator.AssembleProjectAsync(project.Id, None);
        var taken = (await _operator.NextQueuedProjectAsync(None)).ShouldNotBeNull();
        var package = Rendered(taken.Package!) with { Title = "My title", Description = "My description", Tags = ["mine"], MetadataEdited = true };
        var changed = field switch
        {
            "title" => package with { Title = "Why rivers bend" },
            "description" => package with { Description = "Meanders explained." },
            _ => package with { Tags = ["mine", "rivers"] },
        };

        var ex = await Should.ThrowAsync<OperatorTamperingException>(() =>
            _operator.SaveProjectFromWorkerAsync(taken with { Status = "assembled", Package = changed }, null, None));
        var saved = await _operator.SaveProjectFromWorkerAsync(taken with { Status = "assembled", Package = package }, null, None);

        ex.Message.ShouldBe($"The owner edited the package metadata of project '{taken.Id}'; the worker may not change it.");
        (saved.Package!.Title, saved.Package.Description, saved.Package.Tags.Single()).ShouldBe(("My title", "My description", "mine"));
    }

    [Fact]
    public async Task Until_The_Owner_Edits_It_The_Worker_Sets_The_Title_Description_And_Tags()
    {
        var project = await AssembledAsync();

        (project.Package!.Title, project.Package.Description, string.Join("|", project.Package.Tags), project.Package.MetadataEdited)
            .ShouldBe(("Why rivers bend", "Meanders explained.", "rivers|geography", false));
    }

    [Theory]
    [InlineData("media_ready", "assembling")]
    [InlineData("media_ready", "assembled")]
    [InlineData("media_ready", "package")]
    [InlineData("assembly_queued", "failed")]
    [InlineData("assembled", "package")]
    [InlineData("assembled", "assembled")]
    [InlineData("approved", "failed")]
    public async Task A_Worker_Save_Of_Assembly_On_A_Project_That_Is_Not_Assembling_Is_Refused(string stored, string sent)
    {
        var taken = await AssemblingAsync();
        var assembled = await _operator.SaveProjectFromWorkerAsync(
            taken with { Status = "assembled", Package = Rendered(taken.Package!) }, null, None);
        await SetStoredAsync(taken.Id, p => p with { Status = stored });
        var before = await StoredAsync(taken.Id);
        var save = sent switch
        {
            "package" => assembled with { Status = "failed", Package = assembled.Package! with { DurationMs = 7000 } },
            _ => assembled with { Status = sent },
        };

        var ex = await Should.ThrowAsync<OperatorTamperingException>(() =>
            _operator.SaveProjectFromWorkerAsync(save, null, None));

        ex.Message.ShouldBe("The project is no longer assembling.");
        var after = await StoredAsync(taken.Id);
        (after.Status, after.UpdatedAt).ShouldBe((before.Status, before.UpdatedAt));
        Normal(after.Package).ShouldBeEquivalentTo(Normal(before.Package));
    }

    [Theory]
    [InlineData("assembling")]
    [InlineData("assembled")]
    [InlineData("package")]
    public async Task A_Project_The_Worker_Starts_Cannot_Start_Assembled_Or_With_A_Package(string sent)
    {
        await GrantYouTubeAsync();
        var draft = sent == "package"
            ? NewDraft("Glaciers explained", "researching") with { Package = PackageRules.Queue(null) with { Status = "assembling" } }
            : NewDraft("Glaciers explained", sent);

        var ex = await Should.ThrowAsync<OperatorTamperingException>(() => _operator.SaveProjectFromWorkerAsync(draft, null, None));

        ex.Message.ShouldBe("The project is no longer assembling.");
        (await _operator.ListProjectsAsync(0, None)).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_Worker_Save_Without_A_Package_Keeps_The_Stored_Package()
    {
        var project = await AssembledAsync();
        await SetStoredAsync(project.Id, p => p with { Status = "producing" });

        var saved = await _operator.SaveProjectFromWorkerAsync(project with { Status = "producing", Package = null }, null, None);

        Normal(saved.Package).ShouldBeEquivalentTo(Normal(project.Package));
    }

    [Fact]
    public async Task The_Owner_Edits_The_Metadata_Trimmed_And_It_Is_Marked_As_The_Owners()
    {
        var project = await AssembledAsync();

        var edited = await _operator.EditPackageMetadataAsync(project.Id, "  My title ", " My description ", [" mine ", "rivers"], None);

        (edited.Status, edited.Package!.Status, edited.Package.Title, edited.Package.Description, string.Join("|", edited.Package.Tags), edited.Package.MetadataEdited)
            .ShouldBe(("assembled", "assembled", "My title", "My description", "mine|rivers", true));
    }

    [Theory]
    [InlineData(0, 10, 10, 1, "The title is 1 to 100 characters.")]
    [InlineData(101, 10, 10, 1, "The title is 1 to 100 characters.")]
    [InlineData(10, 5001, 10, 1, "The description is at most 5000 characters.")]
    [InlineData(10, 10, 101, 1, "Each tag is 1 to 100 characters.")]
    [InlineData(10, 10, 0, 1, "Each tag is 1 to 100 characters.")]
    [InlineData(10, 10, 100, 6, "The tags are at most 500 characters together.")]
    [InlineData(0, 5001, 10, 1, "The title is 1 to 100 characters. The description is at most 5000 characters.")]
    public async Task Metadata_Outside_YouTubes_Limits_Is_Refused_And_Not_Saved(int title, int description, int tag, int tags, string message)
    {
        var project = await AssembledAsync();

        var ex = await Should.ThrowAsync<OperatorValidationException>(() => _operator.EditPackageMetadataAsync(
            project.Id, new string('t', title), new string('d', description), [.. Enumerable.Repeat(new string('g', tag), tags)], None));

        ex.Message.ShouldBe(message);
        (await StoredAsync(project.Id)).Package!.MetadataEdited.ShouldBeFalse();
    }

    [Fact]
    public async Task Metadata_At_YouTubes_Limits_Is_Accepted()
    {
        var project = await AssembledAsync();
        var tags = Enumerable.Range(0, 4).Select(i => new string((char)('a' + i), 100)).Append(new string('e', 96)).ToList();

        var edited = await _operator.EditPackageMetadataAsync(project.Id, new string('t', 100), new string('d', 5000), tags, None);

        (edited.Package!.Title.Length, edited.Package.Description.Length, PackageRules.TagsLength(edited.Package.Tags)).ShouldBe((100, 5000, 500));
    }

    [Theory]
    [InlineData(new[] { "rivers", "river bends" }, 6 + 13 + 1)]
    [InlineData(new[] { "a b c" }, 7)]
    [InlineData(new string[0], 0)]
    public void Tags_Are_Counted_The_Way_YouTube_Counts_Them_With_Two_For_A_Tag_With_A_Space_And_A_Comma_Between_Tags(string[] tags, int length)
        => PackageRules.TagsLength(tags).ShouldBe(length);

    [Fact]
    public async Task Tags_Whose_Characters_Fit_500_But_Not_With_YouTubes_Commas_And_Quotes_Are_Refused()
    {
        var project = await AssembledAsync();
        var tags = Enumerable.Range(0, 4).Select(i => new string((char)('a' + i), 100)).Append(new string('r', 90) + " bends").ToList();

        var ex = await Should.ThrowAsync<OperatorValidationException>(() =>
            _operator.EditPackageMetadataAsync(project.Id, "Why rivers bend", string.Empty, tags, None));

        tags.Sum(t => t.Length).ShouldBe(496);
        ex.Message.ShouldBe("The tags are at most 500 characters together.");
    }

    [Fact]
    public async Task Editing_The_Metadata_Of_An_Approved_Package_Sends_It_Back_To_Assembled_Without_Its_Approval()
    {
        var project = await ReadyToApproveAsync();
        await _operator.ApprovePackageAsync(project.Id, None);

        var edited = await _operator.EditPackageMetadataAsync(project.Id, "My title", string.Empty, [], None);

        (edited.Status, edited.Package!.Status, edited.Package.ApprovedAt, edited.Package.RightsConfirmed)
            .ShouldBe(("assembled", "assembled", (DateTimeOffset?)null, true));
    }

    [Theory]
    [InlineData("assembling")]
    [InlineData("assembly_queued")]
    [InlineData("failed")]
    public async Task Metadata_Is_Edited_And_Rights_Confirmed_Only_On_An_Assembled_Package(string status)
    {
        var project = await AssembledAsync();
        await SetStoredAsync(project.Id, p => p with { Status = status });

        var edit = await Should.ThrowAsync<OperatorValidationException>(() =>
            _operator.EditPackageMetadataAsync(project.Id, "My title", string.Empty, [], None));
        var rights = await Should.ThrowAsync<OperatorValidationException>(() => _operator.ConfirmMediaRightsAsync(project.Id, None));

        edit.Message.ShouldBe($"Only the metadata of an assembled or approved package is edited; this one is {status}.");
        rights.Message.ShouldBe($"Media rights are confirmed on an assembled package; this one is {status}.");
        var stored = (await StoredAsync(project.Id)).Package!;
        (stored.MetadataEdited, stored.RightsConfirmed).ShouldBe((false, false));
    }

    [Fact]
    public async Task A_Project_Without_A_Package_Has_No_Metadata_Or_Rights_To_Set()
    {
        var project = await WithScenesAsync(Scene("s1", 1));
        await SetStoredAsync(project.Id, p => p with { Status = "assembled" });

        var ex = await Should.ThrowAsync<OperatorValidationException>(() => _operator.ConfirmMediaRightsAsync(project.Id, None));

        ex.Message.ShouldBe($"Project '{project.Id}' has no package yet; assemble it first.");
    }

    [Fact]
    public async Task Saving_The_Metadata_Records_When_The_Owner_Saved_It()
    {
        var project = await AssembledAsync();
        var before = DateTimeOffset.UtcNow;

        var edited = await _operator.EditPackageMetadataAsync(project.Id, "My title", "My description", ["mine"], None);

        project.Package.ShouldNotBeNull().MetadataSavedAt.ShouldBeNull("the worker's first metadata carries no time of an owner's save");
        edited.Package.ShouldNotBeNull().MetadataSavedAt.ShouldNotBeNull("saving the metadata records when the owner saved it")
            .ShouldBeInRange(before, DateTimeOffset.UtcNow);
        (await StoredAsync(project.Id)).Package!.MetadataSavedAt.ShouldBe(edited.Package.MetadataSavedAt);
    }

    [Fact]
    public async Task Confirming_The_Media_Rights_Records_When_The_Owner_Confirmed_Them()
    {
        var project = await AssembledAsync();
        var before = DateTimeOffset.UtcNow;

        var confirmed = await _operator.ConfirmMediaRightsAsync(project.Id, None);

        project.Package.ShouldNotBeNull().RightsConfirmedAt.ShouldBeNull("rights not confirmed have no confirmation time");
        confirmed.Package.ShouldNotBeNull().RightsConfirmedAt.ShouldNotBeNull("confirming the media rights records when the owner confirmed them")
            .ShouldBeInRange(before, DateTimeOffset.UtcNow);
        (await StoredAsync(project.Id)).Package!.RightsConfirmedAt.ShouldBe(confirmed.Package.RightsConfirmedAt);
    }

    [Fact]
    public async Task An_Edit_That_Changes_The_Video_Takes_Back_The_Rights_With_The_Time_They_Were_Confirmed()
    {
        var project = await ReadyToApproveAsync();

        var produced = await _operator.ProduceProjectAsync(project.Id, string.Empty, string.Empty, string.Empty, None);

        project.Package.ShouldNotBeNull().RightsConfirmedAt.ShouldNotBeNull();
        var package = produced.Package.ShouldNotBeNull();
        (package.RightsConfirmed, package.RightsConfirmedAt).ShouldBe(
            (false, (DateTimeOffset?)null), "rights taken back by a change to the video lose the time they were confirmed");
        (await StoredAsync(project.Id)).Package!.RightsConfirmedAt.ShouldBeNull();
    }

    [Fact]
    public async Task A_Worker_Save_Carrying_Other_Owner_Times_Keeps_The_Stored_Ones()
    {
        var project = await AssembledAsync();
        var edited = await _operator.EditPackageMetadataAsync(project.Id, "My title", "My description", ["mine"], None);
        await _operator.AssembleProjectAsync(project.Id, None);
        var taken = (await _operator.NextQueuedProjectAsync(None)).ShouldNotBeNull();
        var forged = new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var package = Rendered(taken.Package!) with
        {
            Title = "My title",
            Description = "My description",
            Tags = ["mine"],
            MetadataEdited = true,
            MetadataSavedAt = forged,
            RightsConfirmedAt = forged,
        };

        var saved = await _operator.SaveProjectFromWorkerAsync(taken with { Status = "assembled", Package = package }, null, None);

        var savedAt = edited.Package.ShouldNotBeNull().MetadataSavedAt.ShouldNotBeNull();
        var stored = (await StoredAsync(project.Id)).Package.ShouldNotBeNull();
        (stored.MetadataSavedAt, stored.RightsConfirmedAt).ShouldBe(
            (savedAt, (DateTimeOffset?)null), "only the owner's edits set when the metadata was saved and the rights confirmed; a worker write keeps the stored times");
        (saved.Package!.MetadataSavedAt, saved.Package.RightsConfirmedAt).ShouldBe((savedAt, (DateTimeOffset?)null));
        (stored.Status, stored.AssembledAt, stored.ThumbnailDescription).ShouldBe(
            ("assembled", package.AssembledAt, package.ThumbnailDescription), "the worker sets when it assembled the package and what the thumbnail shows");
    }

    [Fact]
    public async Task A_Worker_Save_Is_Refused_Once_The_Owner_Confirmed_The_Rights_Of_An_Assembled_Package()
    {
        var project = await AssembledAsync();
        var confirmed = await _operator.ConfirmMediaRightsAsync(project.Id, None);
        var confirmedAt = confirmed.Package.ShouldNotBeNull().RightsConfirmedAt.ShouldNotBeNull();
        var forged = new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);

        await Should.ThrowAsync<OperatorTamperingException>(() => _operator.SaveProjectFromWorkerAsync(
            confirmed with { Package = confirmed.Package with { RightsConfirmedAt = forged } }, null, None));

        (await StoredAsync(project.Id)).Package.ShouldNotBeNull().RightsConfirmedAt.ShouldBe(
            confirmedAt, "the worker may only save a package while it assembles, so it never reaches one whose rights the owner confirmed");
    }

    [Fact]
    public async Task Confirming_The_Rights_Again_Keeps_The_Time_They_Were_First_Confirmed()
    {
        var project = await AssembledAsync();
        var first = await _operator.ConfirmMediaRightsAsync(project.Id, None);
        var firstAt = first.Package.ShouldNotBeNull().RightsConfirmedAt.ShouldNotBeNull();

        var again = await _operator.ConfirmMediaRightsAsync(project.Id, None);

        again.Package.ShouldNotBeNull().RightsConfirmedAt.ShouldBe(
            firstAt, "a repeated confirmation keeps the time the owner first confirmed the rights");
    }

    [Theory]
    [InlineData("assembling", true, 0)]
    [InlineData("assembled", false, 301)]
    public async Task A_Worker_Save_With_An_Assembly_Time_Before_Assembly_Or_A_Too_Long_Thumbnail_Description_Is_Refused(
        string status, bool timed, int descriptionLength)
    {
        var project = await AssembledAsync();
        await _operator.AssembleProjectAsync(project.Id, None);
        var taken = (await _operator.NextQueuedProjectAsync(None)).ShouldNotBeNull();
        var package = Rendered(taken.Package!) with
        {
            Status = status,
            AssembledAt = timed ? new DateTimeOffset(2026, 11, 14, 14, 12, 0, TimeSpan.Zero) : null,
            ThumbnailDescription = new string('a', descriptionLength),
        };

        await Should.ThrowAsync<OperatorValidationException>(
            () => _operator.SaveProjectFromWorkerAsync(taken with { Status = "assembling", Package = package }, null, None),
            "a package still assembling has no assembly time, and the thumbnail description stays within 300 characters");
    }

    [Fact]
    public async Task A_Thumbnail_Description_Of_300_Characters_Is_Accepted()
    {
        var project = await AssembledAsync();
        await _operator.AssembleProjectAsync(project.Id, None);
        var taken = (await _operator.NextQueuedProjectAsync(None)).ShouldNotBeNull();
        var package = Rendered(taken.Package!) with { ThumbnailDescription = new string('a', 300) };

        await _operator.SaveProjectFromWorkerAsync(taken with { Status = "assembled", Package = package }, null, None);

        (await StoredAsync(project.Id)).Package.ShouldNotBeNull().ThumbnailDescription.ShouldNotBeNull().Length.ShouldBe(
            300, "a thumbnail description of exactly 300 characters is within the limit");
    }

    [Fact]
    public async Task The_Owner_Confirms_The_Media_Rights_Of_An_Assembled_Package()
    {
        var project = await AssembledAsync();

        var confirmed = await _operator.ConfirmMediaRightsAsync(project.Id, None);

        (confirmed.Status, confirmed.Package!.RightsConfirmed).ShouldBe(("assembled", true));
        (await StoredAsync(project.Id)).Package!.RightsConfirmed.ShouldBeTrue();
    }

    [Fact]
    public async Task Approving_A_Package_With_Rights_Confirmed_No_Failed_Check_And_Every_Claim_Decided_Records_The_Owners_Approval()
    {
        var project = await ReadyToApproveAsync();
        await SetStoredAsync(project.Id, p => p with { Claims = [Claim("c1", "supported"), Claim("c2", "waived", "Said as disputed")] });
        var before = DateTimeOffset.UtcNow;

        var approved = await _operator.ApprovePackageAsync(project.Id, None);

        (approved.Status, approved.Package!.Status).ShouldBe(("approved", "approved"));
        approved.Package.ApprovedAt.ShouldNotBeNull().ShouldBeInRange(before, DateTimeOffset.UtcNow);
        (await StoredAsync(project.Id)).Package!.ApprovedAt.ShouldBe(approved.Package.ApprovedAt);
        (await _writes.ListAsync(1, None)).Select(e => (e.FeatureId, e.Action, e.Target, e.Outcome, e.ApprovedBy, e.ActionId)).ShouldBe([
            ("youtube", "Scout's data folder", project.Id, "approved package", (string?)"owner", (string?)null),
        ]);
    }

    [Theory]
    [InlineData("rights", "Confirm the media rights first.")]
    [InlineData("one-failed-check", "1 check failed.")]
    [InlineData("two-failed-checks", "2 checks failed.")]
    [InlineData("pending", "1 claim is not supported or waived.")]
    [InlineData("unsupported", "2 claims are not supported or waived.")]
    [InlineData("all", "Confirm the media rights first. 1 check failed. 1 claim is not supported or waived.")]
    public async Task Approval_Is_Blocked_By_Unconfirmed_Rights_A_Failed_Check_Or_A_Claim_Not_Supported_Or_Waived(string cause, string message)
    {
        static PackageCheck Failed(string name) => new(name, "fail", "Placeholder media.");
        var project = cause switch
        {
            "rights" => await AssembledAsync(),
            "one-failed-check" => await ReadyToApproveAsync(p => p with { Checks = [.. p.Checks, Failed("placeholder")] }),
            "two-failed-checks" => await ReadyToApproveAsync(p => p with { Checks = [Failed("placeholder"), Failed("captions")] }),
            "all" => await AssembledAsync(p => p with { Checks = [Failed("placeholder")] }),
            _ => await ReadyToApproveAsync(),
        };
        await SetStoredAsync(project.Id, p => p with
        {
            Claims = cause switch
            {
                "pending" or "all" => [Claim("c1", "supported"), Claim("c2", "pending")],
                "unsupported" => [Claim("c1", "unsupported"), Claim("c2", "pending"), Claim("c3", "waived", "Disputed")],
                _ => [Claim("c1", "supported")],
            },
        });

        var ex = await Should.ThrowAsync<OperatorValidationException>(() => _operator.ApprovePackageAsync(project.Id, None));

        ex.Message.ShouldBe(message);
        var stored = await StoredAsync(project.Id);
        (stored.Status, stored.Package!.ApprovedAt).ShouldBe(("assembled", (DateTimeOffset?)null));
        (await _writes.ListAsync(1, None)).Single().Outcome.ShouldNotBe("approved package");
    }

    [Theory]
    [InlineData("assembling")]
    [InlineData("approved")]
    [InlineData("media_ready")]
    public async Task Only_An_Assembled_Package_Is_Approved(string status)
    {
        var project = await ReadyToApproveAsync();
        await SetStoredAsync(project.Id, p => p with { Status = status });

        var ex = await Should.ThrowAsync<OperatorValidationException>(() => _operator.ApprovePackageAsync(project.Id, None));

        ex.Message.ShouldBe($"Only an assembled package is approved; this one is {status}.");
        (await StoredAsync(project.Id)).Package!.ApprovedAt.ShouldBeNull();
    }

    [Theory]
    [InlineData("assembly_queued")]
    [InlineData("approved")]
    public async Task The_Worker_Cannot_Set_A_Status_Only_The_Owner_Sets(string status)
    {
        var project = await ScriptReadyAsync();

        var ex = await Should.ThrowAsync<OperatorValidationException>(() =>
            _operator.SaveProjectFromWorkerAsync(project with { Status = status }, null, None));

        ex.Message.ShouldBe($"Status '{status}' is not one the worker may set: researching, scripting, transcribing, script_ready, producing, media_ready, assembling, assembled, failed.");
    }

    [Fact]
    public async Task An_Owner_Regenerate_During_Assembly_Refuses_The_Assemblers_Save_As_No_Longer_Assembling_And_Keeps_The_Regenerate()
    {
        var taken = await AssemblingAsync();
        var regenerated = await _operator.RegenerateSceneAsync(taken.Id, "s1", None);

        var ex = await Should.ThrowAsync<OperatorTamperingException>(() =>
            _operator.SaveProjectFromWorkerAsync(taken with { Status = "assembled", Package = Rendered(taken.Package!) }, null, None));

        ex.Message.ShouldBe("The project is no longer assembling.");
        var stored = await StoredAsync(taken.Id);
        stored.Status.ShouldBe("production_queued");
        stored.Scenes.ShouldBe(regenerated.Scenes);
    }

    [Theory]
    [InlineData("reorder", "media_ready")]
    [InlineData("attach", "media_ready")]
    [InlineData("regenerate", "production_queued")]
    public async Task An_Owner_Scene_Edit_On_An_Approved_Package_Clears_The_Rights_And_The_Approval_Until_It_Is_Assembled_Again(string edit, string status)
    {
        var project = await ReadyToApproveAsync();
        await _operator.ApprovePackageAsync(project.Id, None);
        var media = await _operator.GetMediaDirectoryAsync(project.Id, None);
        await File.WriteAllTextAsync(Path.Combine(media, "mine.png"), "image");

        var edited = edit switch
        {
            "reorder" => await _operator.ReorderScenesAsync(project.Id, ["s2", "s1"], None),
            "lock" => await _operator.SetSceneLockedAsync(project.Id, "s1", true, None),
            "attach" => await _operator.AttachSceneMediaAsync(project.Id, "s1", "visual", "mine.png", None),
            _ => await _operator.RegenerateSceneAsync(project.Id, "s1", None),
        };
        var approve = await Should.ThrowAsync<OperatorValidationException>(() => _operator.ApprovePackageAsync(project.Id, None));
        var rights = await Should.ThrowAsync<OperatorValidationException>(() => _operator.ConfirmMediaRightsAsync(project.Id, None));

        var package = edited.Package.ShouldNotBeNull();
        (edited.Status, package.Status, package.RightsConfirmed, package.ApprovedAt, package.VideoFile)
            .ShouldBe((status, "assembled", false, (DateTimeOffset?)null, (string?)"video.mp4"));
        approve.Message.ShouldBe($"Only an assembled package is approved; this one is {status}.");
        rights.Message.ShouldBe($"Media rights are confirmed on an assembled package; this one is {status}.");
        File.Exists(Path.Combine(media, "video.mp4")).ShouldBeTrue();
    }

    [Theory]
    [InlineData(101, 10, 10, 1, "The title is 1 to 100 characters.")]
    [InlineData(0, 10, 10, 1, "The title is 1 to 100 characters.")]
    [InlineData(10, 5001, 10, 1, "The description is at most 5000 characters.")]
    [InlineData(10, 10, 101, 1, "Each tag is 1 to 100 characters.")]
    [InlineData(10, 10, 100, 6, "The tags are at most 500 characters together.")]
    public async Task Approval_Is_Blocked_By_Metadata_Outside_YouTubes_Limits(int title, int description, int tag, int tags, string message)
    {
        var project = await ReadyToApproveAsync();
        await SetStoredAsync(project.Id, p => p with
        {
            Package = p.Package! with
            {
                Title = new string('t', title),
                Description = new string('d', description),
                Tags = [.. Enumerable.Repeat(new string('g', tag), tags)],
            },
        });

        var ex = await Should.ThrowAsync<OperatorValidationException>(() => _operator.ApprovePackageAsync(project.Id, None));

        ex.Message.ShouldBe(message);
        (await StoredAsync(project.Id)).Package!.ApprovedAt.ShouldBeNull();
    }

    [Theory]
    [InlineData(101, 10, 10, 1, "The title is 1 to 100 characters.")]
    [InlineData(0, 10, 10, 1, "The title is 1 to 100 characters.")]
    [InlineData(10, 5001, 10, 1, "The description is at most 5000 characters.")]
    [InlineData(10, 10, 101, 1, "Each tag is 1 to 100 characters.")]
    [InlineData(10, 10, 100, 6, "The tags are at most 500 characters together.")]
    public async Task The_Workers_Metadata_Outside_YouTubes_Limits_Is_Refused_Once_It_Writes_Any(int title, int description, int tag, int tags, string message)
    {
        var taken = await AssemblingAsync();
        var package = Rendered(taken.Package!) with
        {
            Title = new string('t', title),
            Description = new string('d', description),
            Tags = [.. Enumerable.Repeat(new string('g', tag), tags)],
        };

        var ex = await Should.ThrowAsync<OperatorValidationException>(() =>
            _operator.SaveProjectFromWorkerAsync(taken with { Status = "assembled", Package = package }, null, None));
        var unwritten = await _operator.SaveProjectFromWorkerAsync(taken, null, None);

        ex.Message.ShouldBe(message);
        (unwritten.Status, unwritten.Package!.Title, unwritten.Package.Tags.Count).ShouldBe(("assembling", string.Empty, 0));
    }

    [Fact]
    public async Task Editing_The_Metadata_Runs_The_Metadata_And_Discoverability_Checks_Again_Over_The_Owners_Values_And_Keeps_The_Others()
    {
        await _operator.SaveMandateAsync(Mandate(pillars: ["Geography"]), None);
        var project = await AssembledAsync(p => p with
        {
            Checks = [.. p.Checks, new PackageCheck("metadata", "pass", "worker"), new PackageCheck("discoverability", "pass", "worker")],
        });

        var unfound = await _operator.EditPackageMetadataAsync(project.Id, "You will not believe this", "No list here.", [], None);
        var found = await _operator.EditPackageMetadataAsync(project.Id, "Amazing geography", "Intro\n\n0:00 Start\n0:30 Bends", [], None);
        var byTopic = await _operator.EditPackageMetadataAsync(project.Id, "Rivers explained", "0:00 Start", [], None);

        unfound.Package!.Checks.Select(c => (c.Name, c.Result, c.Detail)).ShouldBe([
            ("duration", "pass", "6 seconds"),
            ("loudness", "warn", "-12 LUFS"),
            ("metadata", "pass", "The title, description and tags fit YouTube's limits."),
            ("discoverability", "warn", "Viewers may not find this video: the title shares no word with the topic or the channel's pillars; the description has no chapters list."),
        ]);
        found.Package!.Checks.Single(c => c.Name == "discoverability").ShouldBe(
            new PackageCheck("discoverability", "pass", "The title matches the topic and the description lists chapters."));
        byTopic.Package!.Checks.Single(c => c.Name == "discoverability").Result.ShouldBe("pass");
        (await StoredAsync(project.Id)).Package!.Checks.Count.ShouldBe(4);
    }

    [Fact]
    public void The_Metadata_Check_Of_Edited_Metadata_Fails_Outside_YouTubes_Limits()
    {
        var checks = PackageRules.WithMetadataChecks([], new string('t', 101), "0:00 Start", ["rivers"], "rivers", []);

        checks.Single(c => c.Name == "metadata").ShouldBe(
            new PackageCheck("metadata", "fail", "YouTube would refuse this metadata: The title is 1 to 100 characters."));
    }

    [Theory]
    [InlineData("assembling", "attach")]
    [InlineData("assembling", "reorder")]
    [InlineData("assembly_queued", "attach")]
    [InlineData("assembly_queued", "reorder")]
    public async Task A_Scene_Edit_That_Changes_The_Video_During_Assembly_Queues_It_Again_So_The_Running_Assembly_Is_No_Longer_Assembling(string stage, string edit)
    {
        var project = await WithScenesAsync(Scene("s1", 1), Scene("s2", 2));
        await _operator.AssembleProjectAsync(project.Id, None);
        var taken = stage == "assembling" ? (await _operator.NextQueuedProjectAsync(None)).ShouldNotBeNull() : await StoredAsync(project.Id);
        var media = await _operator.GetMediaDirectoryAsync(project.Id, None);
        await File.WriteAllTextAsync(Path.Combine(media, "mine.png"), "image");
        foreach (var name in new[] { "video.mp4", "short-1.mp4", "thumb.png" })
        {
            await File.WriteAllTextAsync(Path.Combine(media, name), "media");
        }

        var edited = edit == "attach"
            ? await _operator.AttachSceneMediaAsync(project.Id, "s1", "visual", "mine.png", None)
            : await _operator.ReorderScenesAsync(project.Id, ["s2", "s1"], None);
        var save = await Should.ThrowAsync<OperatorTamperingException>(() =>
            _operator.SaveProjectFromWorkerAsync(taken with { Status = "assembled", Package = Rendered(taken.Package!) with { Status = "assembled" } }, null, None));

        (edited.Status, edited.Package!.Status).ShouldBe(("assembly_queued", "assembly_queued"));
        save.Message.ShouldBe("The project is no longer assembling.");
        var next = (await _operator.NextQueuedProjectAsync(None)).ShouldNotBeNull();
        (next.Id, next.Status).ShouldBe((project.Id, "assembling"));
    }

    [Fact]
    public async Task Regenerating_A_Scene_During_Assembly_Sends_The_Project_To_Production_And_The_Package_Back_To_Queued_For_Assembly()
    {
        var taken = await AssemblingAsync();

        var regenerated = await _operator.RegenerateSceneAsync(taken.Id, "s1", None);

        (regenerated.Status, regenerated.Package!.Status).ShouldBe(("production_queued", "assembly_queued"));
    }

    [Theory]
    [InlineData("lock")]
    [InlineData("unchanged order")]
    public async Task A_Lock_Or_The_Order_The_Scenes_Already_Have_Keeps_The_Approval_The_Rights_And_The_Status(string edit)
    {
        var project = await ReadyToApproveAsync();
        var approved = await _operator.ApprovePackageAsync(project.Id, None);

        var edited = edit == "lock"
            ? await _operator.SetSceneLockedAsync(project.Id, "s1", true, None)
            : await _operator.ReorderScenesAsync(project.Id, ["s1", "s2"], None);

        var package = edited.Package.ShouldNotBeNull();
        (edited.Status, package.Status, package.RightsConfirmed, package.ApprovedAt).ShouldBe(("approved", "approved", true, approved.Package!.ApprovedAt));
    }

    [Theory]
    [InlineData("lock")]
    [InlineData("unchanged order")]
    public async Task A_Lock_Or_The_Order_The_Scenes_Already_Have_During_Assembly_Leaves_The_Assembly_Running(string edit)
    {
        var taken = await AssemblingAsync();

        var edited = edit == "lock"
            ? await _operator.SetSceneLockedAsync(taken.Id, "s1", true, None)
            : await _operator.ReorderScenesAsync(taken.Id, ["s1", "s2"], None);
        var saved = await _operator.SaveProjectFromWorkerAsync(
            taken with { Status = "assembled", Scenes = edited.Scenes, Package = Rendered(taken.Package!) }, null, None);

        (edited.Status, edited.Package!.Status).ShouldBe(("assembling", "assembling"));
        saved.Status.ShouldBe("assembled");
    }

    [Fact]
    public async Task Undelegated_media_ready_project_is_stored_as_media_ready()
    {
        var saved = await WithScenesAsync(Scene("s1", 1), Scene("s2", 2));

        saved.Status.ShouldBe("media_ready");
        (await StoredAsync(saved.Id)).Status.ShouldBe("media_ready");
        (await _operator.NextQueuedProjectAsync(None)).ShouldBeNull();
    }

    private async Task<OperatorProject> DelegatedAndAssembledAsync(params Claim[] claims)
    {
        var project = await ScriptReadyAsync(claims);
        await _operator.DelegateProjectAsync(project.Id, string.Empty, string.Empty, string.Empty, None);
        var producing = (await _operator.NextQueuedProjectAsync(None)).ShouldNotBeNull();
        await _operator.SaveProjectFromWorkerAsync(producing with { Status = "media_ready", Scenes = [Scene("s1", 1), Scene("s2", 2)] }, null, None);
        var assembling = (await _operator.NextQueuedProjectAsync(None)).ShouldNotBeNull();
        var media = await _operator.GetMediaDirectoryAsync(project.Id, None);
        foreach (var name in new[] { "video.mp4", "short-1.mp4", "thumb.png" })
        {
            await File.WriteAllTextAsync(Path.Combine(media, name), "media");
        }

        return await _operator.SaveProjectFromWorkerAsync(
            assembling with { Status = "assembled", Package = Rendered(assembling.Package.ShouldNotBeNull()) }, null, None);
    }

    [Fact]
    public async Task Delegated_project_goes_from_media_ready_to_assembly_queued()
    {
        var project = await ScriptReadyAsync();

        var delegated = await _operator.DelegateProjectAsync(project.Id, string.Empty, string.Empty, string.Empty, None);
        var producing = (await _operator.NextQueuedProjectAsync(None)).ShouldNotBeNull();
        var mediaSaved = await _operator.SaveProjectFromWorkerAsync(
            producing with { Status = "media_ready", Scenes = [Scene("s1", 1), Scene("s2", 2)] }, null, None);
        var assembling = (await _operator.NextQueuedProjectAsync(None)).ShouldNotBeNull();
        var media = await _operator.GetMediaDirectoryAsync(project.Id, None);
        foreach (var name in new[] { "video.mp4", "short-1.mp4", "thumb.png" })
        {
            await File.WriteAllTextAsync(Path.Combine(media, name), "media");
        }
        var assembled = await _operator.SaveProjectFromWorkerAsync(
            assembling with { Status = "assembled", Package = Rendered(assembling.Package.ShouldNotBeNull()) }, null, None);

        (delegated.Status, delegated.Delegated).ShouldBe(("production_queued", true));
        (producing.Id, producing.Status).ShouldBe((project.Id, "producing"));
        (mediaSaved.Status, mediaSaved.Package?.Status).ShouldBe(("assembly_queued", "assembly_queued"));
        (assembling.Id, assembling.Status).ShouldBe((project.Id, "assembling"));
        (assembled.Status, assembled.Package!.Status, assembled.Package.ApprovedAt).ShouldBe(("assembled", "assembled", (DateTimeOffset?)null));
        var stored = await StoredAsync(project.Id);
        (stored.Status, stored.Delegated, stored.Package!.ApprovedAt).ShouldBe(("assembled", true, (DateTimeOffset?)null));
        (await _operator.NextQueuedProjectAsync(None)).ShouldBeNull();
    }

    [Fact]
    public async Task Delegate_never_creates_publish_action()
    {
        var assembled = await DelegatedAndAssembledAsync();

        (await _actions.ListPendingAsync(None)).ShouldBeEmpty();
        (await _actions.ListOpenAsync(FeatureCatalog.YouTubeId, None)).ShouldBeEmpty();
        (await _writes.ListAsync(50, None)).Select(e => (e.Outcome, e.ApprovedBy, e.ActionId)).ShouldBe([
            ("saved assembled", (string?)null, (string?)null),
            ("saved assembling", null, null),
            ("saved assembly_queued", null, null),
            ("saved producing", null, null),
            ("saved script_ready", null, null),
            ("saved researching", null, null),
        ]);
        var stored = await StoredAsync(assembled.Id);
        (stored.Status, stored.Package!.Status, stored.Publication).ShouldBe(("assembled", "assembled", (Publication?)null));
    }

    [Fact]
    public async Task Delegate_queues_production_with_pending_claims()
    {
        var project = await ScriptReadyAsync(Claim("c1", "pending"), Claim("c2", "unsupported"));
        var manual = await Should.ThrowAsync<OperatorValidationException>(() => _operator.ProduceProjectAsync(project.Id, string.Empty, string.Empty, string.Empty, None));

        var delegated = await _operator.DelegateProjectAsync(project.Id, string.Empty, string.Empty, string.Empty, None);

        manual.Message.ShouldBe("1 claim is still pending; decide it before production.");
        delegated.Status.ShouldBe("production_queued");
        (await StoredAsync(project.Id)).Status.ShouldBe("production_queued");
    }

    [Fact]
    public async Task A_delegated_project_with_a_pending_claim_is_assembled_and_its_approval_is_still_blocked_by_the_claim()
    {
        var assembled = await DelegatedAndAssembledAsync(Claim("c1", "pending"));
        await _operator.ConfirmMediaRightsAsync(assembled.Id, None);

        var ex = await Should.ThrowAsync<OperatorValidationException>(() => _operator.ApprovePackageAsync(assembled.Id, None));

        assembled.Status.ShouldBe("assembled");
        ex.Message.ShouldBe("1 claim is not supported or waived.");
        var stored = await StoredAsync(assembled.Id);
        (stored.Status, stored.Package!.ApprovedAt).ShouldBe(("assembled", (DateTimeOffset?)null));
    }

    [Fact]
    public async Task Delegate_is_refused_without_the_grant()
    {
        var project = await ScriptReadyAsync();
        await RevokeYouTubeAsync();

        var ex = await Should.ThrowAsync<OperatorWriteRefusedException>(() => _operator.DelegateProjectAsync(project.Id, string.Empty, string.Empty, string.Empty, None));

        ex.Message.ShouldBe(NotAllowed);
        var stored = await StoredAsync(project.Id);
        (stored.Status, stored.Delegated).ShouldBe(("script_ready", false));
    }

    [Theory]
    [InlineData("script_ready", "production_queued")]
    [InlineData("failed", "production_queued")]
    [InlineData("media_ready", "assembly_queued")]
    [InlineData("media_ready with a scene not ready", "production_queued")]
    public async Task Delegating_queues_the_next_reversible_stage_and_clears_the_error(string from, string expected)
    {
        var project = await ScriptReadyAsync();
        var script = new Script("Rivers bend.", [new ScriptSection("Meanders", "Water erodes the outer bank.")], "Subscribe.", [], string.Empty, [], []);
        await SetStoredAsync(project.Id, p => p with
        {
            Status = from.StartsWith("media_ready", StringComparison.Ordinal) ? "media_ready" : from,
            Script = script,
            Error = "Speech timed out.",
            Scenes = from == "script_ready" ? [] : [Scene("s1", 1), Scene("s2", 2, status: from == "media_ready" ? "ready" : "planned")],
        });

        var delegated = await _operator.DelegateProjectAsync(project.Id, string.Empty, string.Empty, string.Empty, None);

        var stored = await StoredAsync(project.Id);
        (delegated.Status, delegated.Delegated, delegated.Error).ShouldBe((expected, true, (string?)null));
        (stored.Status, stored.Delegated, stored.Error).ShouldBe((expected, true, (string?)null));
        (stored.Package?.Status).ShouldBe(expected == "assembly_queued" ? "assembly_queued" : null);
    }

    [Theory]
    [InlineData("queued")]
    [InlineData("assembled")]
    [InlineData("failed without a script")]
    public async Task Only_a_project_with_its_script_ready_its_media_ready_or_a_failed_one_with_a_script_is_delegated(string status)
    {
        var project = await ScriptReadyAsync();
        var stored = status == "failed without a script" ? "failed" : status;
        var script = new Script("Rivers bend.", [new ScriptSection("Meanders", "Water erodes the outer bank.")], "Subscribe.", [], string.Empty, [], []);
        await SetStoredAsync(project.Id, p => p with { Status = stored, Script = status == "assembled" ? script : null });

        var ex = await Should.ThrowAsync<OperatorValidationException>(() => _operator.DelegateProjectAsync(project.Id, string.Empty, string.Empty, string.Empty, None));

        ex.Message.ShouldBe($"Only a project with its script ready, its media ready, or a failed one with a script is delegated; this one is {stored}.");
        var after = await StoredAsync(project.Id);
        (after.Status, after.Delegated).ShouldBe((stored, false));
    }

    [Fact]
    public async Task A_delegated_media_ready_save_with_a_scene_not_ready_is_stored_as_media_ready()
    {
        var project = await ScriptReadyAsync();
        await _operator.DelegateProjectAsync(project.Id, string.Empty, string.Empty, string.Empty, None);
        var producing = (await _operator.NextQueuedProjectAsync(None)).ShouldNotBeNull();

        var saved = await _operator.SaveProjectFromWorkerAsync(
            producing with { Status = "media_ready", Scenes = [Scene("s1", 1), Scene("s2", 2, status: "failed")] }, null, None);

        (saved.Status, saved.Package).ShouldBe(("media_ready", (VideoPackage?)null));
        (await StoredAsync(project.Id)).Status.ShouldBe("media_ready");
        (await _operator.NextQueuedProjectAsync(None)).ShouldBeNull();
    }

    [Fact]
    public async Task Produce_OnADelegatedProject_KeepsItDelegated()
    {
        var project = await ScriptReadyAsync();
        await _operator.DelegateProjectAsync(project.Id, string.Empty, string.Empty, string.Empty, None);
        var taken = (await _operator.NextQueuedProjectAsync(None)).ShouldNotBeNull();
        await _operator.SaveProjectFromWorkerAsync(taken with { Status = "media_ready" }, null, None);

        var produced = await _operator.ProduceProjectAsync(project.Id, string.Empty, string.Empty, string.Empty, None);

        (produced.Status, produced.Delegated).ShouldBe(("production_queued", true));
        (await StoredAsync(project.Id)).Delegated.ShouldBeTrue();
    }

    [Theory]
    [InlineData("  piper:en_GB-cori-medium  ", "piper:en_GB-cori-medium")]
    [InlineData("OpenAI:nova", "OpenAI:nova")]
    [InlineData("", "")]
    public async Task Delegate_StoresTheNarrationVoiceOnTheProject(string chosen, string kept)
    {
        var project = await ScriptReadyAsync();

        var delegated = await _operator.DelegateProjectAsync(project.Id, chosen, string.Empty, string.Empty, None);

        (delegated.NarrationVoice, delegated.Delegated).ShouldBe((kept, true));
        (await StoredAsync(project.Id)).NarrationVoice.ShouldBe(kept);
    }

    [Theory]
    [InlineData("piper:en GB")]
    [InlineData("OpenAI:nova;rm")]
    public async Task Delegate_RefusesAMalformedNarrationVoice(string chosen)
    {
        var project = await ScriptReadyAsync();
        var tooLong = "piper:" + new string('a', 75);

        var malformed = await Should.ThrowAsync<OperatorValidationException>(() =>
            _operator.DelegateProjectAsync(project.Id, chosen, string.Empty, string.Empty, None));
        var overLimit = await Should.ThrowAsync<OperatorValidationException>(() =>
            _operator.DelegateProjectAsync(project.Id, tooLong, string.Empty, string.Empty, None));

        malformed.Message.ShouldBe("A narration voice is an id of at most 80 letters, digits, _ : . # or -.");
        overLimit.Message.ShouldBe("A narration voice is an id of at most 80 letters, digits, _ : . # or -.");
        var stored = await StoredAsync(project.Id);
        (stored.Status, stored.Delegated, stored.NarrationVoice).ShouldBe(("script_ready", false, string.Empty));
    }

    [Fact]
    public async Task The_worker_can_never_delegate_a_project_or_take_a_delegation_back()
    {
        var undelegated = await TakenForProductionAsync();
        var delegatedProject = await ScriptReadyAsync();
        await _operator.DelegateProjectAsync(delegatedProject.Id, string.Empty, string.Empty, string.Empty, None);
        var delegatedTaken = (await _operator.NextQueuedProjectAsync(None)).ShouldNotBeNull();

        var selfDelegated = await _operator.SaveProjectFromWorkerAsync(
            undelegated with { Status = "media_ready", Scenes = [Scene("s1", 1)], Delegated = true }, null, None);
        var undone = await _operator.SaveProjectFromWorkerAsync(
            delegatedTaken with { Status = "media_ready", Scenes = [Scene("s1", 1)], Delegated = false }, null, None);
        var created = await _operator.SaveProjectFromWorkerAsync(NewDraft("Glaciers explained", "researching") with { Delegated = true }, "discord:1:2", None);

        (selfDelegated.Status, selfDelegated.Delegated).ShouldBe(("media_ready", false));
        (undone.Status, undone.Delegated).ShouldBe(("assembly_queued", true));
        created.Delegated.ShouldBeFalse();
        ((await StoredAsync(undelegated.Id)).Delegated, (await StoredAsync(delegatedProject.Id)).Delegated, (await StoredAsync(created.Id)).Delegated)
            .ShouldBe((false, true, false));
    }

    [Fact]
    public async Task Delete_RemovesTheProjectAndItsMediaFolder()
    {
        var project = await ScriptReadyAsync();
        var directory = await _operator.GetMediaDirectoryAsync(project.Id, None);
        await File.WriteAllTextAsync(Path.Combine(directory, "a.mp3"), "audio");

        await _operator.DeleteProjectAsync(project.Id, None);

        await Should.ThrowAsync<OperatorNotFoundException>(() => _operator.GetProjectAsync(project.Id, None));
        Directory.Exists(directory).ShouldBeFalse();
        Directory.Exists(_store.MediaRoot).ShouldBeTrue();
        (await _writes.ListAsync(1, None)).Select(e => (e.Target, e.Outcome, e.ApprovedBy)).ShouldBe([
            (project.Id, "deleted project", (string?)"owner"),
        ]);
    }

    [Fact]
    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macos")]
    public async Task Delete_ReportsAMediaFolderItCouldNotDelete_AfterRemovingTheProject()
    {
        var project = await ScriptReadyAsync();
        var directory = await _operator.GetMediaDirectoryAsync(project.Id, None);
        await File.WriteAllTextAsync(Path.Combine(directory, "a.mp3"), "audio");
        var original = File.GetUnixFileMode(_store.MediaRoot);
        File.SetUnixFileMode(_store.MediaRoot, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            var failure = await Should.ThrowAsync<OperatorStoreException>(() => _operator.DeleteProjectAsync(project.Id, None));

            failure.Message.ShouldBe("Scout removed the project but could not delete its media folder.");
            failure.Message.ShouldNotContain("/");
            failure.Message.ShouldNotContain("\\");
            await Should.ThrowAsync<OperatorNotFoundException>(() => _operator.GetProjectAsync(project.Id, None));
        }
        finally
        {
            File.SetUnixFileMode(_store.MediaRoot, original);
        }
    }

    [Fact]
    public async Task Delete_KeepsOtherProjects()
    {
        var doomed = await ScriptReadyAsync();
        var kept = await _operator.StartProjectAsync("Glaciers explained", string.Empty, string.Empty, None);
        var keptDirectory = await _operator.GetMediaDirectoryAsync(kept.Id, None);
        await File.WriteAllTextAsync(Path.Combine(keptDirectory, "b.mp3"), "audio");
        await _operator.GetMediaDirectoryAsync(doomed.Id, None);

        await _operator.DeleteProjectAsync(doomed.Id, None);

        (await _operator.ListProjectsAsync(0, None)).Select(p => p.Id).ShouldBe([kept.Id]);
        File.Exists(Path.Combine(keptDirectory, "b.mp3")).ShouldBeTrue();
    }

    [Theory]
    [InlineData("researching")]
    [InlineData("scripting")]
    [InlineData("producing")]
    [InlineData("assembling")]
    [InlineData("presenting")]
    [InlineData("uploading")]
    public async Task Delete_IsRefusedWhileRunning(string status)
    {
        var project = await ScriptReadyAsync();
        await SetStoredAsync(project.Id, p => status == "uploading"
            ? p with { Publication = new Publication("uploading", null, "private", null, false, false, null, null, [], [], null) }
            : p with { Status = status });

        var ex = await Should.ThrowAsync<OperatorValidationException>(() => _operator.DeleteProjectAsync(project.Id, None));

        ex.Message.ShouldBe("A project that is researching, scripting, transcribing, producing, assembling, presenting or uploading cannot be deleted; wait for it to finish or fail.");
        (await StoredAsync(project.Id)).Id.ShouldBe(project.Id);
    }

    [Theory]
    [InlineData("")]
    [InlineData(":title:a")]
    public async Task Delete_IsRefusedWithPendingActions(string suffix)
    {
        var project = await ScriptReadyAsync();
        var directory = await _operator.GetMediaDirectoryAsync(project.Id, None);
        await _actions.EnqueueApprovedAsync(FeatureCatalog.YouTubeId, [("videos.update", project.Id + suffix, "Update")], None);

        var ex = await Should.ThrowAsync<OperatorValidationException>(() => _operator.DeleteProjectAsync(project.Id, None));

        ex.Message.ShouldBe("The project has pending YouTube actions; finish or cancel them first.");
        (await StoredAsync(project.Id)).Id.ShouldBe(project.Id);
        Directory.Exists(directory).ShouldBeTrue();
    }

    [Fact]
    public async Task Delete_UnknownProjectIsNotFound()
    {
        await Should.ThrowAsync<OperatorNotFoundException>(() => _operator.DeleteProjectAsync(Guid.NewGuid().ToString("N"), None));
        await Should.ThrowAsync<OperatorValidationException>(() => _operator.DeleteProjectAsync("../escape", None));
    }

    [Fact]
    public async Task Delete_WhoseRecordCannotBeLogged_LeavesTheProjectAndItsMediaFolder()
    {
        var project = await ScriptReadyAsync();
        var directory = await _operator.GetMediaDirectoryAsync(project.Id, None);
        await File.WriteAllTextAsync(Path.Combine(directory, "a.mp3"), "audio");
        var before = await StoredAsync(project.Id);
        var log = new UnwritableLog();
        var unlogged = new OperatorService(_store, new WriteGateService(new FeatureCatalog(), _states, _actions, log), log);

        var failure = await Should.ThrowAsync<IOException>(() => unlogged.DeleteProjectAsync(project.Id, None));

        failure.Message.ShouldBe("The disk is full.");
        (await _store.ReadProjectsAsync(None)).Select(p => (p.Id, p.Status, p.Topic, p.UpdatedAt))
            .ShouldBe([(before.Id, before.Status, before.Topic, before.UpdatedAt)]);
        File.ReadAllText(Path.Combine(directory, "a.mp3")).ShouldBe("audio");
    }
}
