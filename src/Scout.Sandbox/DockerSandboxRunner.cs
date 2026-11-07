using System.Collections;
using System.ComponentModel;

namespace Scout.Sandbox;

/// <summary>
/// Runs a repository's tests in a disposable container through the <c>docker</c> command line: the snapshot is copied
/// into a fresh work folder under <see cref="SandboxOptions.WorkDirectory"/>, the only mount, at <c>/work</c>; the
/// restore runs with the network, the tests with <c>--network none</c>; both as an unprivileged user on a read-only
/// filesystem with every capability dropped, no new privileges, pids, memory and CPU caps, and only the stack's own
/// variables; the whole run stops at 20 minutes; the output is cut to its last 200,000 characters and kept in a log
/// beside the work folder, which is deleted after the run whatever happened.
/// </summary>
public sealed class DockerSandboxRunner
{
    /// <summary>The answer when the <c>docker</c> command line is missing or its daemon does not answer.</summary>
    public const string DockerMissingReason = "Docker is not installed or not running.";

    /// <summary>The answer when the run reached <see cref="TimeLimit"/>.</summary>
    public const string TimeLimitReason = "The tests ran past the 20-minute limit.";

    internal const string SnapshotMissingReason = "The repository snapshot was not found.";
    internal const string WorkInsideSnapshotReason = "The sandbox folder is inside the repository snapshot, so the copy would never end.";
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

    /// <summary>A runner over the real <c>docker</c> command line and this process's environment.</summary>
    public DockerSandboxRunner(SandboxOptions options, TimeProvider time)
        : this(options, time, SystemProcessRunner.Instance, Environment.GetEnvironmentVariables().Cast<DictionaryEntry>().Select(entry => KeyValuePair.Create((string)entry.Key, entry.Value as string ?? string.Empty)))
    {
    }

    internal DockerSandboxRunner(SandboxOptions options, TimeProvider time, IProcessRunner processes, IEnumerable<KeyValuePair<string, string>> parentEnvironment)
    {
        ArgumentNullException.ThrowIfNull(parentEnvironment);
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
            if (restoreExit != 0)
            {
                return new SandboxResult(true, $"The restore exited with code {restoreExit}; the tests did not run.", stack.Image, command, restoreExit, Seconds(started), Last(output), stack.Note);
            }

            command = testCommand;
            container = $"scout-sandbox-{id}-test";
            var exit = await RunAsync(container, runFolder, stack, command, network: false, offline: true, output, linked.Token).ConfigureAwait(false);
            container = null;
            return new SandboxResult(true, null, stack.Image, command, exit, Seconds(started), Last(output), stack.Note);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && deadline.IsCancellationRequested)
        {
            await KillAsync(container).ConfigureAwait(false);
            return new SandboxResult(false, TimeLimitReason, stack.Image, command, null, Seconds(started), Last(output), stack.Note);
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
                }
                finally
                {
                    DeleteRunFolder(runFolder);
                }
            }
        }
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

    private ProcessCall Cli(IReadOnlyList<string> arguments) => new(_options.DockerPath, arguments, _cliEnvironment);

    private double Seconds(long started) => Math.Round(_time.GetElapsedTime(started).TotalSeconds, 1);

    private static SandboxResult NotRun(string reason) => new(false, reason, null, null, null, 0, string.Empty);

    private static string Last(OutputTail output)
    {
        var text = output.ToString();
        return text.Length > ResultOutputLimit ? text[^ResultOutputLimit..] : text;
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
}
