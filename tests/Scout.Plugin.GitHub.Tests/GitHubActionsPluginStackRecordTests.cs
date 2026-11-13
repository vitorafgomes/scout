using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Scout.Plugin.GitHub.Abstractions;
using Scout.Plugin.GitHub.Tests.Fakes;

namespace Scout.Plugin.GitHub.Tests;

public sealed class GitHubActionsPluginStackRecordTests : IDisposable
{
    private static readonly RepositoryCommit Main = new("main", "abc1234def5678", IsDefaultBranch: true);
    private static readonly DateTimeOffset Now = new(2026, 11, 7, 10, 0, 0, TimeSpan.Zero);

    private static readonly RepositoryStack NodeStack = new(
        "Node", "node:20@sha256:8f69", "Node 20.20.2", [new DeclaredVersion(".nvmrc", "Node 20")], null);

    private readonly string _dir = Directory.CreateTempSubdirectory("scout-github-stack-").FullName;
    private readonly IGitHubClient _reads = Substitute.For<IGitHubClient>();
    private readonly IStackDetector _stacks = Substitute.For<IStackDetector>();
    private readonly ISandboxRunner _sandbox = Substitute.For<ISandboxRunner>();
    private readonly FakeTimeProvider _time = new(Now);
    private readonly RecordingLogger<GitHubActionsPlugin> _logger = new();

    public GitHubActionsPluginStackRecordTests()
    {
        _reads.ResolveCommitAsync("octocat", "hello", Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(Main);
        _reads.GetPullRequestHeadAsync("octocat", "hello", 42, Arg.Any<CancellationToken>()).Returns(new PullRequestHead(Main.Sha, "alice"));
        _reads.DownloadTarballAsync("octocat", "hello", Main.Sha, Arg.Any<Func<Stream, CancellationToken, Task>>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                await call.ArgAt<Func<Stream, CancellationToken, Task>>(3)(new MemoryStream(TarGzFixture.Build(TarGzFixture.Text("package.json", "{}"))), CancellationToken.None);
                return true;
            });
        _stacks.Detect(Arg.Any<string>()).Returns(NodeStack);
        _sandbox.RunTestsAsync(Arg.Any<SandboxRunRequest>(), Arg.Any<CancellationToken>())
            .Returns(new SandboxRunResult(true, null, "node:20@sha256:8f69", "npm test", 0, 3, "ok"));
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Snapshots => Path.Combine(_dir, "snapshots");

    private string RecordPath => Path.Combine(Snapshots, "octocat__hello.stack.json");

    private GitHubActionsPlugin Plugin(bool withDetector = true) => new(
        _reads, Substitute.For<IGitHubWriteClient>(), new GitHubProfileStore(Path.Combine(_dir, "profiles")),
        new RepositorySnapshotStore(Snapshots, 0, _time), Substitute.For<IRepositoryProjects>(), _time,
        sandbox: _sandbox, stacks: withDetector ? _stacks : null, logger: _logger);

    private JsonElement Record() => JsonDocument.Parse(File.ReadAllText(RecordPath)).RootElement;

    [Fact]
    public async Task A_snapshot_records_the_repository_s_stack_beside_the_snapshots_from_the_detection_on_its_root()
    {
        await Plugin().SnapshotRepositoryAsync("octocat", "hello", "", CancellationToken.None);

        _stacks.Received(1).Detect(Path.Combine(Snapshots, "octocat__hello", Main.Sha));
        var record = Record();
        record.EnumerateObject().Select(p => p.Name).ShouldBe(
            ["repository", "commit", "stack", "image", "runs", "declared", "refusal", "detectedAt"]);
        record.GetProperty("repository").GetString().ShouldBe("octocat/hello");
        record.GetProperty("commit").GetString().ShouldBe(Main.Sha);
        record.GetProperty("stack").GetString().ShouldBe("Node");
        record.GetProperty("image").GetString().ShouldBe("node:20@sha256:8f69");
        record.GetProperty("runs").GetString().ShouldBe("Node 20.20.2");
        var declared = record.GetProperty("declared").EnumerateArray().ShouldHaveSingleItem();
        (declared.GetProperty("source").GetString(), declared.GetProperty("version").GetString()).ShouldBe((".nvmrc", "Node 20"));
        record.GetProperty("refusal").ValueKind.ShouldBe(JsonValueKind.Null);
        record.GetProperty("detectedAt").GetDateTimeOffset().ShouldBe(Now);
    }

    [Fact]
    public async Task A_reused_snapshot_records_the_stack_again_with_the_new_time()
    {
        var plugin = Plugin();
        await plugin.SnapshotRepositoryAsync("octocat", "hello", "", CancellationToken.None);
        _time.Advance(TimeSpan.FromHours(1));

        var second = await plugin.SnapshotRepositoryAsync("octocat", "hello", "main", CancellationToken.None);

        second.ShouldStartWith("Already have a snapshot of octocat/hello");
        _stacks.Received(2).Detect(Arg.Any<string>());
        Record().GetProperty("detectedAt").GetDateTimeOffset().ShouldBe(Now.AddHours(1));
    }

    [Fact]
    public async Task A_version_Scout_does_not_run_is_recorded_as_the_stack_and_its_refusal_with_no_image()
    {
        _stacks.Detect(Arg.Any<string>()).Returns(new RepositoryStack("Node", null, null, [], "This project asks for Node 16; Scout runs Node 18, 20, 22, 24 and 26."));

        await Plugin().SnapshotRepositoryAsync("octocat", "hello", "", CancellationToken.None);

        var record = Record();
        record.GetProperty("stack").GetString().ShouldBe("Node");
        record.GetProperty("image").ValueKind.ShouldBe(JsonValueKind.Null);
        record.GetProperty("runs").ValueKind.ShouldBe(JsonValueKind.Null);
        record.GetProperty("declared").GetArrayLength().ShouldBe(0);
        record.GetProperty("refusal").GetString().ShouldBe("This project asks for Node 16; Scout runs Node 18, 20, 22, 24 and 26.");
    }

    [Fact]
    public async Task The_snapshot_a_pull_request_s_test_run_takes_records_its_stack_and_the_run_names_the_repository_and_the_pull_request()
    {
        await Plugin().RunPullRequestTestsAsync("octocat", "hello", 42, CancellationToken.None);

        Record().GetProperty("commit").GetString().ShouldBe(Main.Sha);
        await _sandbox.Received(1).RunTestsAsync(
            Arg.Is<SandboxRunRequest>(r => r.Repository == "octocat/hello" && r.PullRequest == 42), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Without_a_detector_no_record_is_written()
    {
        await Plugin(withDetector: false).SnapshotRepositoryAsync("octocat", "hello", "", CancellationToken.None);

        File.Exists(RecordPath).ShouldBeFalse();
    }

    [Fact]
    public async Task A_record_that_cannot_be_written_is_logged_and_the_snapshot_and_the_test_run_answer_as_before()
    {
        Directory.CreateDirectory(RecordPath);

        var taken = await Plugin().SnapshotRepositoryAsync("octocat", "hello", "", CancellationToken.None);
        var run = JsonDocument.Parse(await Plugin().RunPullRequestTestsAsync("octocat", "hello", 42, CancellationToken.None)).RootElement;

        taken.ShouldStartWith("Took a snapshot of octocat/hello at main (abc1234): 1 files");
        run.GetProperty("ran").GetBoolean().ShouldBeTrue();
        run.GetProperty("exitCode").GetInt32().ShouldBe(0);
        _logger.Entries.Count.ShouldBe(2);
        _logger.Entries.ShouldAllBe(e => e.Level == LogLevel.Warning
            && e.Message.StartsWith("The stack of octocat/hello at abc1234 was not recorded; the snapshot is unchanged: ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_detection_that_throws_is_logged_and_the_snapshot_answers_as_before()
    {
        _stacks.Detect(Arg.Any<string>()).Returns(_ => throw new IOException("disk gone"));

        var taken = await Plugin().SnapshotRepositoryAsync("octocat", "hello", "", CancellationToken.None);

        taken.ShouldStartWith("Took a snapshot of octocat/hello at main (abc1234)");
        File.Exists(RecordPath).ShouldBeFalse();
        _logger.Entries.ShouldHaveSingleItem().ShouldBe((LogLevel.Warning, "The stack of octocat/hello at abc1234 was not recorded; the snapshot is unchanged: disk gone"));
    }
}
