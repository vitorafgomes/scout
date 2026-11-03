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
    [InlineData("published")]
    [InlineData("")]
    public async Task The_Worker_Sets_Only_Researching_Scripting_Script_Ready_Or_Failed(string status)
    {
        var project = await ScriptReadyAsync();

        var ex = await Should.ThrowAsync<OperatorValidationException>(() =>
            _operator.SaveProjectFromWorkerAsync(project with { Status = status }, null, CancellationToken.None));

        ex.Message.ShouldBe($"Status '{status}' is not one the worker may set: researching, scripting, script_ready, failed.");
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
}
