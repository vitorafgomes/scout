using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace Scout.Sandbox.Tests;

public sealed class SandboxRunRecordTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 11, 7, 10, 0, 0, TimeSpan.Zero);

    private readonly string _base = Directory.CreateTempSubdirectory("scout-sandbox-record-").FullName;
    private readonly FakeProcessRunner _processes = new();
    private readonly FakeTimeProvider _time = new(Now);
    private readonly RecordingLogger _logger = new();

    public SandboxRunRecordTests()
    {
        Directory.CreateDirectory(Snapshot);
        File.WriteAllText(Path.Combine(Snapshot, "package.json"), "{\"scripts\":{\"test\":\"node --test\"}}");
        File.WriteAllText(Path.Combine(Snapshot, "package-lock.json"), "{}");
    }

    private string Snapshot => Path.Combine(_base, "snapshot");

    private string WorkRoot => Path.Combine(_base, "data", "sandbox");

    public void Dispose() => Directory.Delete(_base, recursive: true);

    private Task<SandboxResult> Run() =>
        new DockerSandboxRunner(new SandboxOptions { WorkDirectory = WorkRoot }, _time, _processes, [], _logger)
            .RunTestsAsync(new SandboxRequest(Snapshot, Repository: "octocat/hello", PullRequest: 42), CancellationToken.None);

    private JsonElement Record()
    {
        var records = Directory.GetFiles(WorkRoot, "*.run.json");
        records.Length.ShouldBe(1, "one run writes one record");
        var log = records[0][..^".run.json".Length] + ".log";
        File.Exists(log).ShouldBeTrue("the record sits beside the run's log");
        return JsonDocument.Parse(File.ReadAllText(records[0])).RootElement;
    }

    [Theory]
    [InlineData("passed", 0, 0, "passed", 0, null)]
    [InlineData("tests failed", 0, 1, "failed", 1, null)]
    [InlineData("restore failed", 1, 0, "failed", 1, "The restore exited with code 1; the tests did not run.")]
    [InlineData("mount refused", 125, 0, "notRun", null, "Docker cannot see Scout's work folder {0}; with Docker Desktop, add it under Settings, Resources, File sharing.")]
    public async Task Each_run_writes_a_record_beside_its_log_naming_the_repository_the_pull_request_the_stack_the_image_and_how_it_ended(
        string kind, int restoreExit, int testExit, string expected, int? exitCode, string? reason)
    {
        _processes.Behaviour = (call, onOutput, _) =>
        {
            if (FakeProcessRunner.IsRun(call, "restore") && restoreExit == 125)
            {
                onOutput($"docker: Error response from daemon: bind source path does not exist: {FakeProcessRunner.MountSource(call)}\n");
            }

            return Task.FromResult(FakeProcessRunner.IsRun(call, "restore") ? restoreExit : FakeProcessRunner.IsRun(call, "test") ? testExit : 0);
        };

        await Run();

        var record = Record();
        record.GetProperty("repository").GetString().ShouldBe("octocat/hello", kind);
        record.GetProperty("pullRequest").GetInt32().ShouldBe(42, kind);
        record.GetProperty("stack").GetString().ShouldBe("Node", kind);
        record.GetProperty("image").GetString().ShouldBe(SandboxImages.Node, kind);
        record.GetProperty("result").GetString().ShouldBe(expected, kind);
        (record.GetProperty("exitCode").ValueKind == JsonValueKind.Null ? (int?)null : record.GetProperty("exitCode").GetInt32()).ShouldBe(exitCode, kind);
        record.GetProperty("reason").GetString().ShouldBe(reason is null ? null : string.Format(CultureInfo.InvariantCulture, reason, WorkRoot), kind);
        record.GetProperty("durationSeconds").GetDouble().ShouldBe(0, kind);
        record.GetProperty("finishedAt").GetDateTimeOffset().ShouldBe(Now, kind);
        record.EnumerateObject().Select(p => p.Name).ShouldBe(
            ["repository", "pullRequest", "stack", "image", "result", "reason", "exitCode", "durationSeconds", "finishedAt"], kind);
    }

    [Fact]
    public async Task A_run_past_20_minutes_records_that_it_did_not_run_with_the_reason_and_its_duration()
    {
        var hanging = new TaskCompletionSource();
        _processes.Behaviour = async (call, _, ct) =>
        {
            if (FakeProcessRunner.IsRun(call, "test"))
            {
                hanging.SetResult();
                await Task.Delay(Timeout.Infinite, ct);
            }

            return 0;
        };

        var run = Run();
        await hanging.Task.WaitAsync(TimeSpan.FromSeconds(10));
        _time.Advance(TimeSpan.FromMinutes(20));
        await run.WaitAsync(TimeSpan.FromSeconds(10));

        var record = Record();
        record.GetProperty("result").GetString().ShouldBe("notRun");
        record.GetProperty("reason").GetString().ShouldBe("The tests ran past the 20-minute limit.");
        record.GetProperty("exitCode").ValueKind.ShouldBe(JsonValueKind.Null);
        record.GetProperty("durationSeconds").GetDouble().ShouldBe(1200);
        record.GetProperty("finishedAt").GetDateTimeOffset().ShouldBe(Now.AddMinutes(20));
    }

    [Fact]
    public async Task A_run_with_no_repository_named_records_it_as_null()
    {
        await new DockerSandboxRunner(new SandboxOptions { WorkDirectory = WorkRoot }, _time, _processes, [], _logger)
            .RunTestsAsync(new SandboxRequest(Snapshot), CancellationToken.None);

        var record = Record();
        record.GetProperty("repository").ValueKind.ShouldBe(JsonValueKind.Null);
        record.GetProperty("pullRequest").ValueKind.ShouldBe(JsonValueKind.Null);
        record.GetProperty("result").GetString().ShouldBe("passed");
    }

    [Fact]
    public async Task Docker_not_answering_leaves_no_record_because_no_run_started()
    {
        _processes.Behaviour = (call, _, _) => Task.FromResult(call.Arguments[0] == "version" ? 1 : 0);

        var result = await Run();

        result.Reason.ShouldBe(DockerSandboxRunner.DockerMissingReason);
        Directory.Exists(WorkRoot).ShouldBeFalse();
    }

    [Fact]
    public async Task A_record_that_cannot_be_written_is_logged_and_the_run_s_result_and_cleanup_are_unchanged()
    {
        _processes.Behaviour = (call, _, _) =>
        {
            if (FakeProcessRunner.IsRun(call, "test"))
            {
                var id = FakeProcessRunner.Name(call)["scout-sandbox-".Length..^"-test".Length];
                Directory.CreateDirectory(Path.Combine(WorkRoot, $"{id}.run.json"));
            }

            return Task.FromResult(0);
        };

        SandboxResult? outcome = null;
        await Should.NotThrowAsync(async () => outcome = await Run());

        var result = outcome.ShouldNotBeNull();
        (result.Ran, result.ExitCode, result.Reason).ShouldBe((true, (int?)0, (string?)null));
        Directory.GetFiles(WorkRoot, "*.log").Length.ShouldBe(1);
        Directory.GetDirectories(WorkRoot).ShouldAllBe(d => d.EndsWith(".run.json", StringComparison.Ordinal), "the run's work folder is deleted");
        var (level, message) = _logger.Entries.ShouldHaveSingleItem();
        level.ShouldBe(LogLevel.Warning);
        message.ShouldStartWith("The run's record could not be written to ");
        message.ShouldContain("the run's result is unchanged");
    }
}
