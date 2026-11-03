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
public sealed class OperatorServiceTests : IDisposable
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

    private Task GrantYouTubeAsync() =>
        _features.SetWriteGrantAsync(FeatureCatalog.YouTubeId, granted: true, confirmWrites: true, CancellationToken.None);

    private Task RevokeYouTubeAsync() =>
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
        await _operator.StartProjectAsync("Why rivers meander", CancellationToken.None);
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
        var ex = await Should.ThrowAsync<OperatorValidationException>(() => _operator.StartProjectAsync(topic, CancellationToken.None));

        ex.Message.ShouldBe("The topic must be 3 to 200 characters.");
        (await _operator.ListProjectsAsync(0, CancellationToken.None)).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_Topic_Longer_Than_Two_Hundred_Characters_Is_Refused()
    {
        var ex = await Should.ThrowAsync<OperatorValidationException>(() =>
            _operator.StartProjectAsync(new string('x', 201), CancellationToken.None));

        ex.Message.ShouldBe("The topic must be 3 to 200 characters.");
    }

    [Fact]
    public async Task Without_The_Owners_Grant_The_Desktop_Cannot_Start_A_Project()
    {
        var ex = await Should.ThrowAsync<OperatorWriteRefusedException>(() =>
            _operator.StartProjectAsync("Why rivers meander", CancellationToken.None));

        ex.Message.ShouldBe(NotAllowed);
        (await _operator.ListProjectsAsync(0, CancellationToken.None)).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_Project_Started_On_The_Desktop_Is_Queued_With_Its_Trimmed_Topic()
    {
        await GrantYouTubeAsync();
        var started = await _operator.StartProjectAsync("  Why rivers meander ", CancellationToken.None);
        var read = await _operator.GetProjectAsync(started.Id, CancellationToken.None);

        (read.Topic, read.Status, read.Origin, read.Script, read.Claims.Count).ShouldBe(("Why rivers meander", "queued", "desktop", (Script?)null, 0));
    }

    [Fact]
    public async Task Projects_Are_Listed_Newest_First_Up_To_The_Limit()
    {
        await GrantYouTubeAsync();
        foreach (var topic in new[] { "First topic", "Second topic", "Third topic" })
        {
            await _operator.StartProjectAsync(topic, CancellationToken.None);
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
        var queued = await _operator.StartProjectAsync("Why rivers meander", CancellationToken.None);
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

        ex.Message.ShouldBe($"Status '{status}' is not one the worker may set: researching, scripting, script_ready, producing, media_ready, failed.");
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
        var first = await _operator.StartProjectAsync("First topic", CancellationToken.None);
        var second = await _operator.StartProjectAsync("Second topic", CancellationToken.None);

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
        await _operator.StartProjectAsync("The only topic", CancellationToken.None);

        var taken = await Task.WhenAll(Enumerable.Range(0, 32)
            .Select(_ => Task.Run(() => _operator.NextQueuedProjectAsync(CancellationToken.None))));

        taken.OfType<OperatorProject>().ShouldHaveSingleItem().Topic.ShouldBe("The only topic");
    }

    [Fact]
    public async Task The_Worker_Reads_Projects_Newest_First_With_The_Owners_Decisions_Under_The_Grant()
    {
        var older = await ScriptReadyAsync(Claim("c1", "pending"), Claim("c2", "pending"));
        await _operator.DecideClaimAsync(older.Id, "c1", "waived", "Common knowledge", CancellationToken.None);
        var newer = await _operator.StartProjectAsync("Glaciers explained", CancellationToken.None);

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
        var queued = await _operator.StartProjectAsync("Why rivers meander", CancellationToken.None);
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
        await _operator.ProduceProjectAsync(project.Id, CancellationToken.None);
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
        await _operator.ProduceProjectAsync(project.Id, CancellationToken.None);

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

        var produced = await _operator.ProduceProjectAsync(project.Id, CancellationToken.None);

        produced.Status.ShouldBe("production_queued");
        (await StoredAsync(project.Id)).Status.ShouldBe("production_queued");
    }

    [Fact]
    public async Task A_Media_Ready_Project_Is_Produced_Again()
    {
        var project = await WithScenesAsync(Scene("s1", 1));

        (await _operator.ProduceProjectAsync(project.Id, CancellationToken.None)).Status.ShouldBe("production_queued");
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

        var produced = await _operator.ProduceProjectAsync(failed.Id, CancellationToken.None);

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
        await _operator.StartProjectAsync("Why rivers meander", CancellationToken.None);
        var researching = (await _operator.NextQueuedProjectAsync(CancellationToken.None)).ShouldNotBeNull();
        var noScript = await _operator.SaveProjectFromWorkerAsync(researching with { Status = "failed", Error = "No search." }, null, CancellationToken.None);
        var pending = await ScriptReadyAsync(Claim("c1", "pending"));
        pending = await _operator.SaveProjectFromWorkerAsync(
            pending with { Status = "failed", Script = new Script("Rivers bend.", [], "Subscribe.", [], string.Empty, [], []), Error = "Stopped." },
            null,
            CancellationToken.None);

        var withoutScript = await Should.ThrowAsync<OperatorValidationException>(() => _operator.ProduceProjectAsync(noScript.Id, CancellationToken.None));
        var withPending = await Should.ThrowAsync<OperatorValidationException>(() => _operator.ProduceProjectAsync(pending.Id, CancellationToken.None));

        withoutScript.Message.ShouldBe("Only a project with its script ready, its media ready, or a failed one with a script goes to production; this one is failed.");
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

        var ex = await Should.ThrowAsync<OperatorValidationException>(() => _operator.ProduceProjectAsync(project.Id, CancellationToken.None));

        ex.Message.ShouldBe(message);
        (await StoredAsync(project.Id)).Status.ShouldBe("script_ready");
    }

    [Fact]
    public async Task Only_A_Script_Ready_Or_Media_Ready_Project_Is_Produced()
    {
        await GrantYouTubeAsync();
        var queued = await _operator.StartProjectAsync("Why rivers meander", CancellationToken.None);

        var ex = await Should.ThrowAsync<OperatorValidationException>(() => _operator.ProduceProjectAsync(queued.Id, CancellationToken.None));

        ex.Message.ShouldBe("Only a project with its script ready, its media ready, or a failed one with a script goes to production; this one is queued.");
        (await StoredAsync(queued.Id)).Status.ShouldBe("queued");
    }

    [Fact]
    public async Task Without_The_Owners_Grant_A_Project_Is_Not_Produced()
    {
        var project = await ScriptReadyAsync();
        await RevokeYouTubeAsync();

        var ex = await Should.ThrowAsync<OperatorWriteRefusedException>(() => _operator.ProduceProjectAsync(project.Id, CancellationToken.None));

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
    public async Task Media_Is_Attached_Only_As_Audio_Or_Visual()
    {
        var project = await WithScenesAsync(Scene("s1", 1));

        var ex = await Should.ThrowAsync<OperatorValidationException>(() =>
            _operator.AttachSceneMediaAsync(project.Id, "s1", "music", "song.mp3", CancellationToken.None));

        ex.Message.ShouldBe("Media is attached as audio or visual.");
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
            Narration = "Rivers bend, always.",
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
            project with { Scenes = [project.Scenes[0], changed with { Narration = "New narration." }] }, null, CancellationToken.None);

        saved.Scenes.ShouldBe([project.Scenes[0], project.Scenes[1] with { Narration = "New narration." }]);
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
        var regenerated = project.Scenes[1] with { Narration = "New narration." };

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
}
