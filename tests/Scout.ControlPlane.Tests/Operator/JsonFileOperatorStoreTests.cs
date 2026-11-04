using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Scout.ControlPlane.Features;
using Scout.ControlPlane.Operator;
using Shouldly;
using Xunit;

namespace Scout.ControlPlane.Tests.Operator;

public sealed class JsonFileOperatorStoreTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("scout-operator-store-").FullName;
    private readonly RecordingLogger<JsonFileOperatorStore> _logger = new();

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private JsonFileOperatorStore Store() =>
        new(Options.Create(new FeatureStateStoreOptions { FilePath = Path.Combine(_directory, "features.json") }), _logger);

    private static OperatorProject Project(string id, string status) => new(
        id,
        "Why rivers meander",
        status,
        "desktop",
        DateTimeOffset.UnixEpoch,
        DateTimeOffset.UnixEpoch,
        [new TopicCandidate("Meanders", "Fits the science pillar", 0.8)],
        new Script(
            "Rivers never run straight.",
            [new ScriptSection("Intro", "Look at any map.")],
            "Subscribe.",
            ["Why rivers bend"],
            "A short explainer.",
            ["rivers"],
            [new Chapter("0:00", "Intro")]),
        [new Claim("c1", "Most rivers meander.", "low", ["https://example.org/rivers"], "waived", "Common knowledge", DateTimeOffset.UnixEpoch)],
        "none",
        "discord:channel/42");

    [Fact]
    public async Task A_Change_That_Answers_Null_Writes_Nothing()
    {
        using var store = Store();
        await store.UpdateProjectsAsync(projects => { projects.Add(Project("p1", "queued")); return 0; }, CancellationToken.None);
        var written = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(store.ProjectsPath, written);
        var bytes = await File.ReadAllBytesAsync(store.ProjectsPath);

        var result = await store.UpdateProjectsAsync<OperatorProject?>(_ => null, CancellationToken.None);

        result.ShouldBeNull();
        (await File.ReadAllBytesAsync(store.ProjectsPath)).ShouldBe(bytes);
        File.GetLastWriteTimeUtc(store.ProjectsPath).ShouldBe(written);
    }

    [Fact]
    public async Task The_Mandate_And_The_Projects_Survive_A_Reopen_In_Files_Beside_Features_Json()
    {
        var mandate = new Mandate("Teach geography", "Students", ["Rivers"], "weekly", "en", "Calm", ["Politics"], "views", "1000", DateTimeOffset.UnixEpoch);
        var project = Project("p1", "queued");
        using (var store = Store())
        {
            await store.WriteMandateAsync(mandate, CancellationToken.None);
            await store.UpdateProjectsAsync(projects => { projects.Add(project); return 0; }, CancellationToken.None);
        }

        using var reopened = Store();
        var readMandate = (await reopened.ReadMandateAsync(CancellationToken.None)).ShouldNotBeNull();
        var readProject = (await reopened.ReadProjectsAsync(CancellationToken.None)).ShouldHaveSingleItem();

        (reopened.MandatePath, reopened.ProjectsPath).ShouldBe((Path.Combine(_directory, "mandate.json"), Path.Combine(_directory, "projects.json")));
        (readMandate.Objective, readMandate.Pillars.Single(), readMandate.BlockedTopics.Single(), readMandate.UpdatedAt)
            .ShouldBe(("Teach geography", "Rivers", "Politics", (DateTimeOffset?)DateTimeOffset.UnixEpoch));
        (readProject.Id, readProject.Status, readProject.OriginReference, readProject.Error).ShouldBe(("p1", "queued", "discord:channel/42", "none"));
        readProject.Script.ShouldNotBeNull().Chapters.ShouldHaveSingleItem().ShouldBe(new Chapter("0:00", "Intro"));
        var claim = readProject.Claims.ShouldHaveSingleItem();
        (claim.Status, claim.Note, claim.DecidedAt, claim.SourceUrls.Single()).ShouldBe(("waived", "Common knowledge", (DateTimeOffset?)DateTimeOffset.UnixEpoch, "https://example.org/rivers"));
    }

    [Fact]
    public async Task Without_Files_The_Store_Has_No_Mandate_And_No_Projects()
    {
        using var store = Store();

        (await store.ReadMandateAsync(CancellationToken.None)).ShouldBeNull();
        (await store.ReadProjectsAsync(CancellationToken.None)).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_Change_That_Throws_Leaves_The_Projects_File_As_It_Was()
    {
        using var store = Store();
        await store.UpdateProjectsAsync(projects => { projects.Add(Project("p1", "queued")); return 0; }, CancellationToken.None);
        var before = await File.ReadAllBytesAsync(store.ProjectsPath);

        await Should.ThrowAsync<InvalidOperationException>(() => store.UpdateProjectsAsync<int>(
            projects => { projects.Clear(); throw new InvalidOperationException("refused"); },
            CancellationToken.None));

        (await File.ReadAllBytesAsync(store.ProjectsPath)).ShouldBe(before);
    }

    [Fact]
    public async Task Concurrent_Updates_Never_Hand_The_Same_Queued_Project_To_Two_Callers()
    {
        using var store = Store();
        await store.UpdateProjectsAsync(
            projects => { projects.AddRange(Enumerable.Range(0, 5).Select(i => Project($"p{i}", "queued"))); return 0; },
            CancellationToken.None);

        var taken = await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => Task.Run(() => store.UpdateProjectsAsync(
            projects =>
            {
                var index = projects.FindIndex(p => p.Status == "queued");
                if (index < 0)
                {
                    return null;
                }

                projects[index] = projects[index] with { Status = "researching" };
                return projects[index].Id;
            },
            CancellationToken.None))));

        taken.OfType<string>().Order().ShouldBe(["p0", "p1", "p2", "p3", "p4"]);
        (await store.ReadProjectsAsync(CancellationToken.None)).ShouldAllBe(p => p.Status == "researching");
    }

    [Theory]
    [InlineData("mandate.json")]
    [InlineData("projects.json")]
    public async Task A_Malformed_File_Is_Moved_Aside_Logged_Once_And_The_Store_Starts_Empty(string name)
    {
        using var store = Store();
        var path = Path.Combine(_directory, name);
        var corrupt = "{ broken"u8.ToArray();
        await File.WriteAllBytesAsync(path, corrupt);

        var mandate = await store.ReadMandateAsync(CancellationToken.None);
        var projects = await store.ReadProjectsAsync(CancellationToken.None);

        mandate.ShouldBeNull();
        projects.ShouldBeEmpty();
        var movedAside = Directory.GetFiles(_directory, name + ".corrupt-*").ShouldHaveSingleItem();
        (await File.ReadAllBytesAsync(movedAside)).ShouldBe(corrupt);
        File.Exists(path).ShouldBeFalse();
        var warning = _logger.Entries.ShouldHaveSingleItem();
        warning.Level.ShouldBe(LogLevel.Warning);
        warning.Message.ShouldBe($"Operator file at {path} is malformed; moved it to {movedAside} and started empty");
    }

    [Fact]
    public async Task A_Projects_File_That_Cannot_Be_Opened_Propagates_The_Error_And_Is_Never_Moved_Aside()
    {
        using var store = Store();
        var content = "[]"u8.ToArray();
        await File.WriteAllBytesAsync(store.ProjectsPath, content);

        await using (new FileStream(store.ProjectsPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            await Should.ThrowAsync<IOException>(() => store.ReadProjectsAsync(CancellationToken.None));
        }

        Directory.GetFiles(_directory, "projects.json.corrupt-*").ShouldBeEmpty();
        (await File.ReadAllBytesAsync(store.ProjectsPath)).ShouldBe(content);
        _logger.Entries.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_Projects_File_From_Before_Scenes_Reads_With_No_Scenes_And_Scenes_Survive_A_Reopen()
    {
        await File.WriteAllTextAsync(
            Path.Combine(_directory, "projects.json"),
            """[{"id":"p1","topic":"Why rivers meander","status":"script_ready","origin":"desktop","createdAt":"2026-11-01T00:00:00+00:00","updatedAt":"2026-11-01T00:00:00+00:00","candidates":[],"claims":[]}]""");
        var scene = new Scene("s1", 1, "Rivers bend.", "A river from above", true, "ready", null, "owner", "generated", "voice.mp3", "river.png", 4200);

        using (var store = Store())
        {
            (await store.ReadProjectsAsync(CancellationToken.None)).ShouldHaveSingleItem().Scenes.ShouldBeEmpty();
            await store.UpdateProjectsAsync(projects => { projects[0] = projects[0] with { Scenes = [scene] }; return 0; }, CancellationToken.None);
        }

        using var reopened = Store();
        (await reopened.ReadProjectsAsync(CancellationToken.None)).ShouldHaveSingleItem().Scenes.ShouldHaveSingleItem().ShouldBe(scene);
    }

    [Fact]
    public async Task A_Projects_File_From_Before_Formats_Reads_As_Generated_Pictures_With_Its_Visual_Brief_As_The_Direction()
    {
        await File.WriteAllTextAsync(
            Path.Combine(_directory, "projects.json"),
            """[{"id":"p1","topic":"Why rivers meander","status":"media_ready","origin":"desktop","createdAt":"2026-11-01T00:00:00+00:00","updatedAt":"2026-11-01T00:00:00+00:00","candidates":[],"claims":[],"visualBrief":"Hand-drawn watercolour.","motion":"still"}]""");

        using (var store = Store())
        {
            var read = (await store.ReadProjectsAsync(CancellationToken.None)).ShouldHaveSingleItem();
            (read.Format, read.Direction, read.Motion).ShouldBe(("generated_pictures", "Hand-drawn watercolour.", "still"));
            await store.UpdateProjectsAsync(projects => { projects[0] = projects[0] with { Direction = "Calm and slow." }; return 0; }, CancellationToken.None);
        }

        var written = await File.ReadAllTextAsync(Path.Combine(_directory, "projects.json"));
        written.ShouldContain("\"visualBrief\": \"Calm and slow.\"");
        written.ShouldContain("\"format\": \"generated_pictures\"");
        written.ShouldNotContain("\"direction\"");
        using var reopened = Store();
        (await reopened.ReadProjectsAsync(CancellationToken.None)).ShouldHaveSingleItem().Direction.ShouldBe("Calm and slow.");
    }

    [Fact]
    public async Task The_Recording_Clips_Transcript_Times_And_Transcription_Readiness_Survive_A_Restart()
    {
        var scene = new Scene("s1", 1, "Hello from the river.", string.Empty, false, "ready", null, "recording", "recording", null, null, 4200)
        {
            ClipStartMs = 1500,
            ClipEndMs = 5700,
        };
        var project = Project("p1", "media_ready") with
        {
            Format = "own_video",
            RecordingFile = "owner-recording-1a2b3c4d.mp4",
            RecordingMs = 600_000,
            Script = new Script(string.Empty, [new ScriptSection("Intro", "Hello from the river.") { StartMs = 1500, EndMs = 5700 }], string.Empty, [], string.Empty, [], []),
            Scenes = [scene],
        };

        using (var store = Store())
        {
            await store.UpdateProjectsAsync(projects => { projects.Add(project); return 0; }, CancellationToken.None);
            await store.WriteReadinessAsync(new Readiness(false, false, "ollama", true, false, []) { TranscriptionAvailable = true }, CancellationToken.None);
        }

        using var reopened = Store();
        var read = (await reopened.ReadProjectsAsync(CancellationToken.None)).ShouldHaveSingleItem();
        (read.Format, read.RecordingFile, read.RecordingMs).ShouldBe(("own_video", (string?)"owner-recording-1a2b3c4d.mp4", 600_000));
        read.Script.ShouldNotBeNull().Sections.ShouldHaveSingleItem().ShouldBe(new ScriptSection("Intro", "Hello from the river.") { StartMs = 1500, EndMs = 5700 });
        read.Scenes.ShouldHaveSingleItem().ShouldBe(scene);
        (await reopened.ReadReadinessAsync(CancellationToken.None)).ShouldNotBeNull().TranscriptionAvailable.ShouldBeTrue();
    }

    [Fact]
    public async Task The_Readiness_Report_Survives_A_Reopen_In_Readiness_Json_Beside_Features_Json()
    {
        var report = new Readiness(true, false, "openai", true, false, ["No image model is configured."], DateTimeOffset.UnixEpoch);

        using (var store = Store())
        {
            (await store.ReadReadinessAsync(CancellationToken.None)).ShouldBeNull();
            await store.WriteReadinessAsync(report, CancellationToken.None);
            store.ReadinessPath.ShouldBe(Path.Combine(_directory, "readiness.json"));
        }

        using var reopened = Store();
        var read = (await reopened.ReadReadinessAsync(CancellationToken.None)).ShouldNotBeNull();
        (read.SpeechAvailable, read.ImagesAvailable, read.Provider, read.FfmpegAvailable, read.YouTubeSignedIn, read.ReportedAt)
            .ShouldBe((true, false, "openai", true, false, (DateTimeOffset?)DateTimeOffset.UnixEpoch));
        read.Notes.ShouldBe(["No image model is configured."]);
    }

    [Fact]
    public async Task Comments_Start_Empty_Without_A_File_And_Survive_A_Reopen_In_Comments_Json_Beside_Features_Json()
    {
        var comment = new AudienceComment(
            "c1", "t1", "v1", "Why rivers bend", "Ana", "How long did this take?", DateTimeOffset.UnixEpoch, 3,
            "question", "effort", "drafted", "About a week.", null, null, null, null, DateTimeOffset.UnixEpoch);
        var idea = new AudienceIdea("idea-c1", "A video on deltas", "c1", "proposed", null);

        using (var store = Store())
        {
            (await store.ReadCommentsAsync(CancellationToken.None)).ShouldBe(CommentsState.Empty);
            await store.UpdateCommentsAsync(_ => new CommentsState([comment], [idea], DateTimeOffset.UnixEpoch, null, "quota"), CancellationToken.None);
        }

        using var reopened = Store();
        var read = await reopened.ReadCommentsAsync(CancellationToken.None);

        reopened.CommentsPath.ShouldBe(Path.Combine(_directory, "comments.json"));
        read.Comments.ShouldHaveSingleItem().ShouldBe(comment);
        read.Ideas.ShouldHaveSingleItem().ShouldBe(idea);
        (read.SyncedAt, read.SyncRequestedAt, read.SyncError).ShouldBe(((DateTimeOffset?)DateTimeOffset.UnixEpoch, (DateTimeOffset?)null, "quota"));
    }

    [Fact]
    public async Task A_Comments_File_That_Cannot_Be_Written_Fails_With_A_Readable_Sentence_And_Keeps_The_Earlier_One()
    {
        using var store = Store();
        await store.UpdateCommentsAsync(_ => CommentsState.Empty with { SyncError = "first" }, CancellationToken.None);
        var before = await File.ReadAllBytesAsync(store.CommentsPath);
        Directory.CreateDirectory(store.CommentsPath + ".tmp");

        var ex = await Should.ThrowAsync<OperatorStoreException>(() =>
            store.UpdateCommentsAsync(_ => CommentsState.Empty with { SyncError = "second" }, CancellationToken.None));

        ex.Message.ShouldBe("Scout could not save the comments in its data folder.");
        (await File.ReadAllBytesAsync(store.CommentsPath)).ShouldBe(before);
    }

    [Fact]
    public async Task Comments_json_stores_non_ascii_text_unescaped()
    {
        const string text = "Привет, сколько времени это заняло? 🎉";
        var comment = new AudienceComment(
            "c1", "t1", "v1", "Почему реки извиваются", "Аня", text, DateTimeOffset.UnixEpoch, 0,
            "question", "время", "new", string.Empty, null, null, null, null, DateTimeOffset.UnixEpoch);
        using var store = Store();

        await store.UpdateCommentsAsync(_ => CommentsState.Empty with { Comments = [comment] }, CancellationToken.None);
        var raw = await File.ReadAllTextAsync(store.CommentsPath);
        await File.WriteAllTextAsync(
            store.CommentsPath,
            raw.Replace("Привет, сколько времени это заняло?", "\\u041F\\u0440\\u0438\\u0432\\u0435\\u0442", StringComparison.Ordinal));
        var escaped = (await store.ReadCommentsAsync(CancellationToken.None)).Comments.ShouldHaveSingleItem();

        raw.ShouldContain("Привет, сколько времени это заняло?");
        raw.ShouldContain("Почему реки извиваются");
        raw.ShouldContain("\\uD83C\\uDF89");
        raw.ShouldNotContain("\\u04");
        escaped.Text.ShouldBe("Привет 🎉");
    }
}
