using System.Collections;
using System.ComponentModel;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Scout.Sandbox;

/// <summary>
/// Runs a repository's tests in a disposable container through the <c>docker</c> command line: the snapshot is copied
/// into a fresh work folder under <see cref="SandboxOptions.WorkDirectory"/>, the only mount, at <c>/work</c>; the
/// restore runs with the network, the tests with <c>--network none</c>; both as an unprivileged user on a read-only
/// filesystem with every capability dropped, no new privileges, pids, memory and CPU caps, and only the stack's own
/// variables; the whole run stops at 20 minutes; the output is cut to its last 200,000 characters and kept in a log
/// beside the work folder, which is deleted after the run whatever happened; beside the log, a small record of the run
/// (<c>&lt;id&gt;.run.json</c>) says which repository and pull request it was, the stack, the image and how it ended; it is
/// first written as <c>running</c> when the test command starts, and a run that ends without a result leaves none.
/// </summary>
public sealed partial class DockerSandboxRunner
{
    /// <summary>The answer when the <c>docker</c> command line is missing or its daemon does not answer.</summary>
    public const string DockerMissingReason = "Docker is not installed or not running.";

    /// <summary>The answer when the run reached <see cref="TimeLimit"/>.</summary>
    public const string TimeLimitReason = "The tests ran past the 20-minute limit.";

    internal const string SnapshotMissingReason = "The repository snapshot was not found.";
    internal const string WorkInsideSnapshotReason = "The sandbox folder is inside the repository snapshot, so the copy would never end.";
    internal const int DockerRunFailed = 125;
    internal const int OutputLimit = 200_000;
    internal const int ResultOutputLimit = 4_000;
    internal const string FallbackUser = "1000:1000";
    internal const string PidsLimit = "512";
    internal const string Memory = "4g";
    internal const string Cpus = "2";
    internal static readonly TimeSpan TimeLimit = TimeSpan.FromMinutes(20);

    /// <summary>
    /// The owner's own uid and gid on Linux, so the container can write the work folder and Scout can delete what it
    /// wrote; 1000:1000 where Docker maps the files itself, and never root.
    /// </summary>
    internal static readonly string User = UserFor(OperatingSystem.IsLinux() && File.Exists("/proc/self/status") ? File.ReadAllText("/proc/self/status") : null);

    private static readonly string[] CliVariables =
    [
        "PATH", "HOME", "USERPROFILE", "SystemRoot", "APPDATA", "LOCALAPPDATA", "ProgramData", "TEMP", "TMP", "TMPDIR",
        "XDG_RUNTIME_DIR", "DOCKER_HOST", "DOCKER_CONTEXT", "DOCKER_CONFIG", "DOCKER_CERT_PATH", "DOCKER_TLS_VERIFY",
    ];

    private readonly SandboxOptions _options;
    private readonly TimeProvider _time;
    private readonly IProcessRunner _processes;
    private readonly IReadOnlyDictionary<string, string> _cliEnvironment;
    private readonly ILogger _logger;

    /// <summary>A runner over the real <c>docker</c> command line and this process's environment.</summary>
    public DockerSandboxRunner(SandboxOptions options, TimeProvider time, ILogger<DockerSandboxRunner>? logger = null)
        : this(options, time, SystemProcessRunner.Instance, Environment.GetEnvironmentVariables().Cast<DictionaryEntry>().Select(entry => KeyValuePair.Create((string)entry.Key, entry.Value as string ?? string.Empty)), logger)
    {
    }

    internal DockerSandboxRunner(SandboxOptions options, TimeProvider time, IProcessRunner processes, IEnumerable<KeyValuePair<string, string>> parentEnvironment, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(parentEnvironment);
        _logger = logger ?? NullLogger.Instance;
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _processes = processes ?? throw new ArgumentNullException(nameof(processes));
        _cliEnvironment = parentEnvironment
            .Where(entry => CliVariables.Contains(entry.Key, StringComparer.OrdinalIgnoreCase))
            .ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal);
    }

    /// <summary>Runs the snapshot's tests and says what happened; a cancelled <paramref name="ct"/> kills the container and throws.</summary>
    public async Task<SandboxResult> RunTestsAsync(SandboxRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.SnapshotRoot) || !Directory.Exists(request.SnapshotRoot))
        {
            return NotRun(SnapshotMissingReason);
        }

        var stack = SandboxStacks.Detect(request.SnapshotRoot, request.StackHint, out var reason);
        if (stack is null)
        {
            return NotRun(reason);
        }

        var testCommand = string.IsNullOrWhiteSpace(request.Command) ? stack.TestCommand : request.Command.Trim();
        var root = Path.GetFullPath(_options.WorkDirectory);
        var snapshot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(request.SnapshotRoot)) + Path.DirectorySeparatorChar;
        if ((root + Path.DirectorySeparatorChar).StartsWith(snapshot, StringComparison.Ordinal))
        {
            return NotRun(WorkInsideSnapshotReason);
        }

        var id = $"{Guid.NewGuid():N}";
        var runFolder = Path.Combine(root, id);
        var output = new OutputTail(OutputLimit);
        var started = _time.GetTimestamp();
        string? container = null;
        var command = stack.RestoreCommand;
        var note = stack.Note;
        SandboxResult? result = null;
        DateTimeOffset? testStarted = null;
        var recordPath = Path.Combine(root, $"{id}.run.json");
        var running = false;

        using var deadline = new CancellationTokenSource(TimeLimit, _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
        try
        {
            if (!await DockerAnswersAsync(linked.Token).ConfigureAwait(false))
            {
                return NotRun(DockerMissingReason);
            }

            Directory.CreateDirectory(Path.Combine(runFolder, "cache", "home"));
            CopySnapshot(request.SnapshotRoot, Path.Combine(runFolder, "src"));

            container = $"scout-sandbox-{id}-restore";
            var restoreExit = await RunAsync(container, runFolder, stack, command, _options.RestoreNetwork, offline: false, output, linked.Token).ConfigureAwait(false);
            container = null;
            if (restoreExit == DockerRunFailed && IsMountRefused(output.ToString()))
            {
                return result = new SandboxResult(false, MountRefusedReason(root), null, null, null, Seconds(started), Last(output));
            }

            if (restoreExit != 0)
            {
                return result = new SandboxResult(true, $"The restore exited with code {restoreExit}; the tests did not run.", stack.Image, command, restoreExit, Seconds(started), Last(output, failed: true), stack.Note);
            }

            command = testCommand;
            note = stack.LeftOut is not null && testCommand == stack.TestCommand ? $"{stack.Note} {stack.LeftOut}".TrimStart() : stack.Note;
            container = $"scout-sandbox-{id}-test";
            testStarted = _time.GetUtcNow();
            running = await WriteRunRecordAsync(recordPath, new RunRecord(
                request.Repository, request.PullRequest, SandboxStacks.Label(stack.Name), stack.Image, command, "running", null, null, 0, testStarted, null)).ConfigureAwait(false);
            var exit = await RunAsync(container, runFolder, stack, command, network: false, offline: true, output, linked.Token).ConfigureAwait(false);
            container = null;
            return result = new SandboxResult(true, exit is 126 or 127 ? $"The test command could not be started (exit {exit}: a command it names was not found or is not executable)." : null, stack.Image, command, exit, Seconds(started), Last(output, failed: exit != 0), note);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && deadline.IsCancellationRequested)
        {
            await KillAsync(container).ConfigureAwait(false);
            return result = new SandboxResult(false, TimeLimitReason, stack.Image, command, null, Seconds(started), Last(output, failed: true), note);
        }
        catch (OperationCanceledException)
        {
            await KillAsync(container).ConfigureAwait(false);
            throw;
        }
        finally
        {
            if (Directory.Exists(runFolder))
            {
                try
                {
                    await File.WriteAllTextAsync(Path.Combine(root, $"{id}.log"), output.ToString(), CancellationToken.None).ConfigureAwait(false);
                    if (result is not null)
                    {
                        var outcome = !result.Ran ? "notRun" : result.ExitCode == 0 && result.Reason is null ? "passed" : "failed";
                        await WriteRunRecordAsync(recordPath, new RunRecord(
                            request.Repository, request.PullRequest, SandboxStacks.Label(stack.Name), stack.Image, result.Command, outcome, result.Reason, result.ExitCode, result.DurationSeconds, testStarted, _time.GetUtcNow())).ConfigureAwait(false);
                    }
                    else if (running)
                    {
                        DeleteRunRecord(recordPath);
                    }
                }
                finally
                {
                    DeleteRunFolder(runFolder);
                }
            }
        }
    }

    /// <summary>What a run of the snapshot would use, read from its manifests as text; nothing runs and nothing is written.</summary>
    public static SandboxDetection Detect(string snapshotRoot)
    {
        var stack = SandboxStacks.Detect(snapshotRoot, null, out var reason, out var refused);
        return stack is null
            ? new SandboxDetection(refused is null ? null : SandboxStacks.Label(refused), null, null, [], reason)
            : new SandboxDetection(SandboxStacks.Label(stack.Name), stack.Image, stack.Runs, stack.Declared ?? [], null);
    }

    internal static List<string> RunArguments(string container, string runFolder, SandboxStack stack, string command, bool network, bool offline)
    {
        var arguments = new List<string> { "run", "--rm", "--name", container };
        if (!network)
        {
            arguments.AddRange(["--network", "none"]);
        }

        arguments.AddRange(
        [
            "--user", User,
            "--read-only",
            "--mount", $"type=bind,source={runFolder},target=/work",
            "--tmpfs", "/tmp:rw,exec,nosuid,nodev",
            "--workdir", "/work/src",
            "--cap-drop", "ALL",
            "--security-opt", "no-new-privileges",
            "--pids-limit", PidsLimit,
            "--memory", Memory,
            "--cpus", Cpus,
        ]);
        foreach (var (key, value) in offline ? stack.Environment.Concat(stack.OfflineEnvironment) : stack.Environment)
        {
            arguments.AddRange(["-e", $"{key}={value}"]);
        }

        arguments.AddRange([stack.Image, "sh", "-c", command]);
        return arguments;
    }

    internal static string UserFor(string? procStatus)
    {
        string? Field(string name) => procStatus?.Split('\n')
            .FirstOrDefault(line => line.StartsWith(name + ":", StringComparison.Ordinal))?[(name.Length + 1)..]
            .Split(['\t', ' '], StringSplitOptions.RemoveEmptyEntries).ElementAtOrDefault(1);

        var uid = Field("Uid");
        var gid = Field("Gid");
        return uid is null || gid is null || uid == "0" || gid == "0" || !uid.All(char.IsAsciiDigit) || !gid.All(char.IsAsciiDigit)
            ? FallbackUser
            : $"{uid}:{gid}";
    }

    private async Task<bool> DockerAnswersAsync(CancellationToken ct)
    {
        try
        {
            return await _processes.RunAsync(Cli(["version", "--format", "{{.Server.Version}}"]), _ => { }, ct).ConfigureAwait(false) == 0;
        }
        catch (Win32Exception)
        {
            return false;
        }
    }

    private async Task<int> RunAsync(string container, string runFolder, SandboxStack stack, string command, bool network, bool offline, OutputTail output, CancellationToken ct)
    {
        output.Append($"$ {command}\n");
        return await _processes.RunAsync(Cli(RunArguments(container, runFolder, stack, command, network, offline)), output.Append, ct).ConfigureAwait(false);
    }

    private async Task KillAsync(string? container)
    {
        if (container is null)
        {
            return;
        }

        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            await _processes.RunAsync(Cli(["kill", container]), _ => { }, limit.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is Win32Exception or OperationCanceledException)
        {
        }
    }

    private async Task<bool> WriteRunRecordAsync(string path, RunRecord record)
    {
        try
        {
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(record, RecordJson), CancellationToken.None).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("The run's record could not be written to {Path}; the run's result is unchanged: {Reason}", path, ex.Message);
            return false;
        }
    }

    private void DeleteRunRecord(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("The record of a run that ended without a result could not be deleted from {Path}: {Reason}", path, ex.Message);
        }
    }

    private ProcessCall Cli(IReadOnlyList<string> arguments) => new(_options.DockerPath, arguments, _cliEnvironment);

    private double Seconds(long started) => Math.Round(_time.GetElapsedTime(started).TotalSeconds, 1);

    internal static string MountRefusedReason(string workDirectory) =>
        $"Docker cannot see Scout's work folder {workDirectory}; with Docker Desktop, add it under Settings, Resources, File sharing.";

    private static bool IsMountRefused(string output) =>
        output.Contains("bind source path does not exist", StringComparison.Ordinal)
        || output.Contains("mounts denied", StringComparison.OrdinalIgnoreCase);

    private static SandboxResult NotRun(string reason) => new(false, reason, null, null, null, 0, string.Empty);

    private static string Last(OutputTail output, bool failed = false)
    {
        var text = output.ToString();
        return text.Length <= ResultOutputLimit ? text : failed ? WithFailureLines(text) : text[^ResultOutputLimit..];
    }

    private static void CopySnapshot(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var entry in new DirectoryInfo(source).EnumerateFileSystemInfos())
        {
            if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                continue;
            }

            var destination = Path.Combine(target, entry.Name);
            if (entry is DirectoryInfo directory)
            {
                CopySnapshot(directory.FullName, destination);
            }
            else
            {
                ((FileInfo)entry).CopyTo(destination);
            }
        }
    }

    private static void DeleteRunFolder(string folder)
    {
        if (!OperatingSystem.IsWindows())
        {
            var walk = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint };
            foreach (var directory in Directory.EnumerateDirectories(folder, "*", walk).Prepend(folder))
            {
                File.SetUnixFileMode(directory, File.GetUnixFileMode(directory) | UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }

        Directory.Delete(folder, recursive: true);
    }

    private static readonly JsonSerializerOptions RecordJson = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    /// <summary>
    /// The record of one run, written beside its log: the command that ran last, null when none started; the result,
    /// <c>running</c> while the test command runs, then <c>passed</c>, <c>failed</c> or <c>notRun</c>; when the test command
    /// started, null when it never did; and when the run ended, null while it runs.
    /// </summary>
    private sealed record RunRecord(
        string? Repository,
        int? PullRequest,
        string Stack,
        string Image,
        string? Command,
        string Result,
        string? Reason,
        int? ExitCode,
        double DurationSeconds,
        DateTimeOffset? StartedAt,
        DateTimeOffset? FinishedAt);

    /// <summary>The last characters written, bounded while reading so a test that prints without end cannot fill memory.</summary>
    private sealed class OutputTail(int capacity)
    {
        private readonly Queue<string> _chunks = new();
        private readonly Lock _gate = new();
        private int _length;

        public void Append(string text)
        {
            lock (_gate)
            {
                _chunks.Enqueue(text);
                _length += text.Length;
                while (_chunks.Count > 1 && _length - _chunks.Peek().Length >= capacity)
                {
                    _length -= _chunks.Dequeue().Length;
                }
            }
        }

        public override string ToString()
        {
            lock (_gate)
            {
                var text = string.Concat(_chunks);
                return text.Length > capacity ? text[^capacity..] : text;
            }
        }
    }

    /// <summary>The most a failed run's result can carry: the heading, the failure lines, the marker, the second heading and the tail.</summary>
    internal const int FailedOutputLimit = 6_500;

    internal const int FailureLinesLimit = 2_000;
    internal const string FailuresHeading = "Failure lines found earlier in the output:\n";
    internal const string TailHeading = "\nEnd of the output:\n";

    [System.Text.RegularExpressions.GeneratedRegex(@"\[FAIL\]|^\s*Failed |Failed!|\berror [A-Z]+[0-9]+\b|^\s*FAIL\b|--- FAIL|Assertion[A-Za-z]*Error|\bFAILED\b|<<< FAILURE!|^\[ERROR\] Tests run:|^E {2,}\S|panicked at|^\s*\u25CF ")]
    private static partial System.Text.RegularExpressions.Regex FailureLine();

    [System.Text.RegularExpressions.GeneratedRegex(@"Error Message:|^\s*failed \S")]
    private static partial System.Text.RegularExpressions.Regex FailureWithTwoLines();

    /// <summary>
    /// A failed run's output past its tail: the lines before the tail that name a failure, the first ones up to
    /// <see cref="FailureLinesLimit"/> characters with how many were left out, then the tail; the tail alone when none does.
    /// </summary>
    private static string WithFailureLines(string text)
    {
        var lines = text[..^ResultOutputLimit].Split('\n');
        var found = new List<string>();
        for (var i = 0; i < lines.Length; i++)
        {
            if (FailureWithTwoLines().IsMatch(lines[i]))
            {
                found.Add(string.Join('\n', lines.Skip(i).Take(3)));
                i += 2;
            }
            else if (FailureLine().IsMatch(lines[i]))
            {
                found.Add(lines[i]);
            }
        }

        var tail = text[^ResultOutputLimit..];
        if (found.Count == 0)
        {
            return tail;
        }

        var entries = found.Distinct(StringComparer.Ordinal).ToList();
        var kept = 0;
        for (var used = 0; kept < entries.Count && used + entries[kept].Length + 1 <= FailureLinesLimit; kept++)
        {
            used += entries[kept].Length + 1;
        }

        var leftOut = entries.Skip(kept).Sum(entry => entry.AsSpan().Count('\n') + 1);
        return FailuresHeading
            + string.Concat(entries.Take(kept).Select(entry => entry + "\n"))
            + (leftOut > 0 ? $"({leftOut.ToString(System.Globalization.CultureInfo.InvariantCulture)} more failure lines left out)\n" : string.Empty)
            + TailHeading
            + tail;
    }
}
