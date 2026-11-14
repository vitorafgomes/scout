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

    private static readonly string[] Fields =
        ["repository", "pullRequest", "stack", "image", "command", "result", "reason", "exitCode", "durationSeconds", "startedAt", "finishedAt"];

    private string RecordPath(ProcessCall call) =>
        Path.Combine(WorkRoot, $"{FakeProcessRunner.Name(call)["scout-sandbox-".Length..^"-test".Length]}.run.json");

    private static DateTimeOffset? Time(JsonElement record, string name) =>
        record.GetProperty(name).ValueKind == JsonValueKind.Null ? null : record.GetProperty(name).GetDateTimeOffset();

    private JsonElement Record()
    {
        var records = Directory.GetFiles(WorkRoot, "*.run.json");
        records.Length.ShouldBe(1, "one run writes one record");
        var log = records[0][..^".run.json".Length] + ".log";
        File.Exists(log).ShouldBeTrue("the record sits beside the run's log");
        return JsonDocument.Parse(File.ReadAllText(records[0])).RootElement;
    }

    [Theory]
    [InlineData("passed", 0, 0, "passed", 0, null, "npm test", true)]
    [InlineData("tests failed", 0, 1, "failed", 1, null, "npm test", true)]
    [InlineData("restore failed", 1, 0, "failed", 1, "The restore exited with code 1; the tests did not run.", "npm ci", false)]
    [InlineData("mount refused", 125, 0, "notRun", null, "Docker cannot see Scout's work folder {0}; with Docker Desktop, add it under Settings, Resources, File sharing.", null, false)]
    public async Task Each_run_writes_a_record_beside_its_log_naming_the_repository_the_pull_request_the_stack_the_image_the_command_and_how_it_ended(
        string kind, int restoreExit, int testExit, string expected, int? exitCode, string? reason, string? command, bool testsStarted)
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
        record.GetProperty("command").GetString().ShouldBe(command, $"{kind}: the sandbox records the command it ran, the restore's when the restore failed, none when Docker refused the mount");
        record.GetProperty("result").GetString().ShouldBe(expected, kind);
        (record.GetProperty("exitCode").ValueKind == JsonValueKind.Null ? (int?)null : record.GetProperty("exitCode").GetInt32()).ShouldBe(exitCode, kind);
        record.GetProperty("reason").GetString().ShouldBe(reason is null ? null : string.Format(CultureInfo.InvariantCulture, reason, WorkRoot), kind);
        record.GetProperty("durationSeconds").GetDouble().ShouldBe(0, kind);
        Time(record, "startedAt").ShouldBe(testsStarted ? Now : null, $"{kind}: the record says when the test command started, null when it never did");
        record.GetProperty("finishedAt").GetDateTimeOffset().ShouldBe(Now, kind);
        record.EnumerateObject().Select(p => p.Name).ShouldBe(Fields, kind);
    }

    [Fact]
    public async Task While_the_test_command_runs_the_record_says_running_with_the_test_command_and_when_it_started()
    {
        JsonElement? running = null;
        _processes.Behaviour = (call, _, _) =>
        {
            if (FakeProcessRunner.IsRun(call, "restore"))
            {
                _time.Advance(TimeSpan.FromMinutes(1));
            }

            if (FakeProcessRunner.IsRun(call, "test"))
            {
                running = File.Exists(RecordPath(call)) ? JsonDocument.Parse(File.ReadAllText(RecordPath(call))).RootElement.Clone() : null;
                _time.Advance(TimeSpan.FromMinutes(2));
            }

            return Task.FromResult(0);
        };

        await Run();

        var record = running.ShouldNotBeNull("while the tests run in the container, the record of the run already exists");
        record.GetProperty("result").GetString().ShouldBe("running", "while the tests run, the record says the run is running");
        record.GetProperty("command").GetString().ShouldBe("npm test", "while the tests run, the record names the test command");
        Time(record, "startedAt").ShouldBe(Now.AddMinutes(1), "while the tests run, the record says when the test command started");
        record.GetProperty("repository").GetString().ShouldBe("octocat/hello");
        record.GetProperty("pullRequest").GetInt32().ShouldBe(42);
        record.GetProperty("stack").GetString().ShouldBe("Node");
        record.GetProperty("image").GetString().ShouldBe(SandboxImages.Node);
        record.GetProperty("reason").ValueKind.ShouldBe(JsonValueKind.Null);
        record.GetProperty("exitCode").ValueKind.ShouldBe(JsonValueKind.Null);
        record.GetProperty("durationSeconds").GetDouble().ShouldBe(0);
        Time(record, "finishedAt").ShouldBeNull("a running run has not finished");
        record.EnumerateObject().Select(p => p.Name).ShouldBe(Fields);
    }

    [Fact]
    public async Task The_final_record_keeps_when_the_test_command_started_and_says_how_the_run_ended()
    {
        _processes.Behaviour = (call, _, _) =>
        {
            _time.Advance(TimeSpan.FromMinutes(FakeProcessRunner.IsRun(call, "restore") ? 1 : FakeProcessRunner.IsRun(call, "test") ? 2 : 0));
            return Task.FromResult(FakeProcessRunner.IsRun(call, "test") ? 1 : 0);
        };

        await Run();

        var record = Record();
        record.GetProperty("result").GetString().ShouldBe("failed");
        record.GetProperty("command").GetString().ShouldBe("npm test");
        record.GetProperty("exitCode").GetInt32().ShouldBe(1);
        record.GetProperty("durationSeconds").GetDouble().ShouldBe(180);
        Time(record, "startedAt").ShouldBe(Now.AddMinutes(1), "the final record keeps when the test command started");
        Time(record, "finishedAt").ShouldBe(Now.AddMinutes(3), "the final record says when the run ended");
    }

    [Fact]
    public async Task A_restore_that_fails_writes_no_running_record_because_the_tests_never_started()
    {
        _processes.Behaviour = (call, _, _) => Task.FromResult(FakeProcessRunner.IsRun(call, "restore") ? 1 : 0);

        await Run();

        _processes.Runs.ShouldHaveSingleItem();
        var record = Record();
        record.GetProperty("result").GetString().ShouldBe("failed");
        record.GetProperty("command").GetString().ShouldBe("npm ci");
        Time(record, "startedAt").ShouldBeNull("a restore failure writes no running record: the test command never started");
        _logger.Entries.ShouldBeEmpty("no record write was attempted, so nothing failed to be written");
    }

    [Fact]
    public async Task A_run_the_owner_stops_during_the_tests_leaves_no_running_record()
    {
        using var stop = new CancellationTokenSource();
        string? during = null;
        _processes.Behaviour = async (call, _, ct) =>
        {
            if (FakeProcessRunner.IsRun(call, "test"))
            {
                during = File.Exists(RecordPath(call)) ? JsonDocument.Parse(File.ReadAllText(RecordPath(call))).RootElement.GetProperty("result").GetString() : null;
                await stop.CancelAsync();
                await Task.Delay(Timeout.Infinite, ct);
            }

            return 0;
        };

        await Should.ThrowAsync<OperationCanceledException>(() =>
            new DockerSandboxRunner(new SandboxOptions { WorkDirectory = WorkRoot }, _time, _processes, [], _logger)
                .RunTestsAsync(new SandboxRequest(Snapshot, Repository: "octocat/hello", PullRequest: 42), stop.Token));

        during.ShouldBe("running", "the record said running while the tests ran");
        Directory.GetFiles(WorkRoot, "*.run.json").ShouldBeEmpty("a run the owner stopped leaves no running record behind");
        Directory.GetFiles(WorkRoot, "*.log").ShouldHaveSingleItem("a stopped run still keeps its log");
        _logger.Entries.ShouldBeEmpty("deleting the running record of a stopped run logs no failure");
    }

    [Fact]
    public async Task A_running_record_that_cannot_be_deleted_when_the_owner_stops_the_run_is_logged_and_the_stop_still_reaches_the_caller()
    {
        using var stop = new CancellationTokenSource();
        _processes.Behaviour = async (call, _, ct) =>
        {
            if (FakeProcessRunner.IsRun(call, "test"))
            {
                File.Delete(RecordPath(call));
                Directory.CreateDirectory(Path.Combine(RecordPath(call), "held"));
                await stop.CancelAsync();
                await Task.Delay(Timeout.Infinite, ct);
            }

            return 0;
        };

        await Should.ThrowAsync<OperationCanceledException>(() =>
            new DockerSandboxRunner(new SandboxOptions { WorkDirectory = WorkRoot }, _time, _processes, [], _logger)
                .RunTestsAsync(new SandboxRequest(Snapshot, Repository: "octocat/hello", PullRequest: 42), stop.Token));

        var (level, message) = _logger.Entries.ShouldHaveSingleItem("the failed delete is logged once");
        level.ShouldBe(LogLevel.Warning);
        message.ShouldStartWith("The record of a run that ended without a result could not be deleted from ", Case.Sensitive, "the warning names the record a stopped run could not delete");
    }

    [Fact]
    public async Task A_running_record_that_cannot_be_written_is_logged_and_the_tests_still_run()
    {
        _processes.Behaviour = (call, _, _) =>
        {
            if (FakeProcessRunner.IsRun(call, "restore"))
            {
                var id = FakeProcessRunner.Name(call)["scout-sandbox-".Length..^"-restore".Length];
                Directory.CreateDirectory(Path.Combine(WorkRoot, $"{id}.run.json"));
            }

            return Task.FromResult(0);
        };

        SandboxResult? outcome = null;
        await Should.NotThrowAsync(async () => outcome = await Run());

        _processes.Runs.Count.ShouldBe(2, "a running record that cannot be written does not stop the tests");
        var result = outcome.ShouldNotBeNull();
        (result.Ran, result.Command, result.ExitCode, result.Reason).ShouldBe((true, (string?)"npm test", (int?)0, (string?)null));
        _logger.Entries.Count.ShouldBe(2, "the running record and the final record each fail to be written, and each is logged");
        _logger.Entries.ShouldAllBe(entry => entry.Level == LogLevel.Warning && entry.Message.StartsWith("The run's record could not be written to ", StringComparison.Ordinal), "each failed record write is a warning that names the record");
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
        record.GetProperty("result").GetString().ShouldBe("notRun", "a run cut by the time limit leaves no running record: the final record replaces it");
        record.GetProperty("command").GetString().ShouldBe("npm test", "the record names the command that was running at the time limit");
        record.GetProperty("reason").GetString().ShouldBe("The tests ran past the 20-minute limit.");
        record.GetProperty("exitCode").ValueKind.ShouldBe(JsonValueKind.Null);
        record.GetProperty("durationSeconds").GetDouble().ShouldBe(1200);
        Time(record, "startedAt").ShouldBe(Now, "a run cut by the time limit keeps when its test command started");
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
                File.Delete(RecordPath(call));
                Directory.CreateDirectory(RecordPath(call));
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
