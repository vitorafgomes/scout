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
}
