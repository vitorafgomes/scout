using System.ComponentModel;
using Microsoft.Extensions.Time.Testing;

namespace Scout.Sandbox.Tests;

public sealed class DockerSandboxRunnerTests : IDisposable
{
    private const string FakeSecret = "ghp_fake-owner-secret-0123456789";

    private readonly string _base = Directory.CreateTempSubdirectory("scout-sandbox-").FullName;
    private readonly FakeProcessRunner _processes = new();
    private readonly FakeTimeProvider _time = new();
    private readonly Dictionary<string, string> _parentEnvironment = new(StringComparer.Ordinal)
    {
        ["PATH"] = "/usr/local/bin:/usr/bin",
        ["HOME"] = "/home/owner",
        ["DOCKER_HOST"] = "unix:///var/run/docker.sock",
        ["SCOUT_FAKE_TOKEN"] = FakeSecret,
        ["GITHUB_TOKEN"] = FakeSecret,
    };

    public DockerSandboxRunnerTests()
    {
        Directory.CreateDirectory(Snapshot);
        File.WriteAllText(Path.Combine(Snapshot, "package.json"), "{\"scripts\":{\"test\":\"node --test\"}}");
        File.WriteAllText(Path.Combine(Snapshot, "package-lock.json"), "{}");
        Directory.CreateDirectory(Path.Combine(Snapshot, "test"));
        File.WriteAllText(Path.Combine(Snapshot, "test", "hello.test.js"), "require('node:test')");
    }

    private string Snapshot => Path.Combine(_base, "snapshot");

    private string WorkRoot => Path.Combine(_base, "data", "sandbox");

    public void Dispose() => Directory.Delete(_base, recursive: true);

    private DockerSandboxRunner Runner(bool restoreNetwork = true) =>
        new(new SandboxOptions { WorkDirectory = WorkRoot, DockerPath = "docker", RestoreNetwork = restoreNetwork }, _time, _processes, _parentEnvironment);

    private Task<SandboxResult> Run(SandboxRequest? request = null, bool restoreNetwork = true, CancellationToken ct = default) =>
        Runner(restoreNetwork).RunTestsAsync(request ?? new SandboxRequest(Snapshot), ct);

    private string[] RunFoldersLeft() =>
        Directory.Exists(WorkRoot) ? Directory.GetDirectories(WorkRoot) : [];

    [Fact]
    public async Task A_run_asks_docker_whether_it_answers_then_restores_then_tests()
    {
        var result = await Run();

        _processes.Calls.Select(call => call.FileName).ShouldAllBe(name => name == "docker");
        _processes.Calls[0].Arguments.ShouldBe(["version", "--format", "{{.Server.Version}}"]);
        FakeProcessRunner.IsRun(_processes.Calls[1], "restore").ShouldBeTrue();
        FakeProcessRunner.IsRun(_processes.Calls[2], "test").ShouldBeTrue();
        _processes.Calls.Count.ShouldBe(3);
        result.ShouldBe(new SandboxResult(true, null, SandboxImages.Node, "npm test", 0, 0, "$ npm ci\n$ npm test\n"));
    }

    [Fact]
    public async Task The_work_folder_under_the_configured_root_is_the_only_mount_at_work_and_holds_the_snapshot_copy()
    {
        string[]? copied = null;
        _processes.Behaviour = (call, _, _) =>
        {
            if (FakeProcessRunner.IsRun(call, "restore"))
            {
                var source = FakeProcessRunner.MountSource(call);
                copied = [.. Directory.EnumerateFiles(Path.Combine(source, "src"), "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(source, f)).Order(StringComparer.Ordinal)];
            }

            return Task.FromResult(0);
        };

        await Run();

        var root = Path.GetFullPath(WorkRoot) + Path.DirectorySeparatorChar;
        foreach (var run in _processes.Runs)
        {
            run.Arguments.Count(a => a == "--mount").ShouldBe(1);
            run.Arguments.ShouldNotContain("-v");
            run.Arguments.ShouldNotContain("--volume");
            run.Arguments.ShouldNotContain("--volumes-from");
            run.Arguments.ShouldNotContain(a => a.StartsWith("--mount=", StringComparison.Ordinal) || a.StartsWith("--volume=", StringComparison.Ordinal));
            FakeProcessRunner.MountSource(run).ShouldStartWith(root);
            Path.GetDirectoryName(FakeProcessRunner.MountSource(run)).ShouldBe(Path.GetFullPath(WorkRoot));
            FakeProcessRunner.Value(run, "--workdir").ShouldBe("/work/src");
        }

        FakeProcessRunner.MountSource(_processes.Restore).ShouldBe(FakeProcessRunner.MountSource(_processes.Test));
        copied.ShouldBe([
            Path.Combine("src", "package-lock.json"),
            Path.Combine("src", "package.json"),
            Path.Combine("src", "test", "hello.test.js"),
        ]);
    }

    [Fact]
    public async Task The_work_folder_is_deleted_after_the_run_and_only_the_log_stays_under_the_root()
    {
        await Run();

        var source = FakeProcessRunner.MountSource(_processes.Test);
        Directory.Exists(source).ShouldBeFalse();
        RunFoldersLeft().ShouldBeEmpty();
        Directory.GetFiles(WorkRoot).Select(Path.GetFileName).ShouldBe([Path.GetFileName(source) + ".log"]);
    }

    [Fact]
    public async Task A_symbolic_link_in_the_snapshot_is_not_copied_into_the_work_folder()
    {
        var outside = Directory.CreateDirectory(Path.Combine(_base, "outside")).FullName;
        File.WriteAllText(Path.Combine(outside, "owner.txt"), "owner");
        File.CreateSymbolicLink(Path.Combine(Snapshot, "passwd"), "/etc/passwd");
        Directory.CreateSymbolicLink(Path.Combine(Snapshot, "home"), outside);
        string[]? copied = null;
        _processes.Behaviour = (call, _, _) =>
        {
            if (FakeProcessRunner.IsRun(call, "restore"))
            {
                copied = [.. Directory.EnumerateFileSystemEntries(Path.Combine(FakeProcessRunner.MountSource(call), "src")).Select(Path.GetFileName)!];
            }

            return Task.FromResult(0);
        };

        await Run();

        copied.ShouldNotBeNull().ShouldNotContain("passwd");
        copied.ShouldNotContain("home");
        copied.ShouldContain("package.json");
    }

    [Fact]
    public async Task The_image_is_the_stacks_allowlisted_image_pinned_by_digest()
    {
        await Run();

        foreach (var run in _processes.Runs)
        {
            var image = run.Arguments[^4];
            image.ShouldBe(SandboxImages.Node);
            SandboxImages.All.ShouldContain(image);
            run.Arguments.TakeLast(3).ShouldBe(["sh", "-c", run.Arguments[^1]]);
        }
    }

    [Fact]
    public async Task The_tests_run_with_network_none_and_the_restore_with_the_network()
    {
        await Run();

        FakeProcessRunner.HasPair(_processes.Test, "--network", "none").ShouldBeTrue();
        _processes.Restore.Arguments.ShouldNotContain("--network");
        _processes.Restore.Arguments[^1].ShouldBe("npm ci");
        _processes.Test.Arguments[^1].ShouldBe("npm test");
    }

    [Fact]
    public async Task With_the_restore_network_turned_off_the_restore_runs_with_network_none_too()
    {
        await Run(restoreNetwork: false);

        FakeProcessRunner.HasPair(_processes.Restore, "--network", "none").ShouldBeTrue();
        FakeProcessRunner.HasPair(_processes.Test, "--network", "none").ShouldBeTrue();
    }

    [Fact]
    public async Task Both_runs_are_removed_after_and_run_as_the_unprivileged_user()
    {
        await Run();

        foreach (var run in _processes.Runs)
        {
            run.Arguments[1].ShouldBe("--rm");
            var user = FakeProcessRunner.Value(run, "--user");
            user.ShouldBe(DockerSandboxRunner.User);
            user.ShouldMatch("^[0-9]+:[0-9]+$");
            user.Split(':').ShouldNotContain("0");
        }
    }

    [Theory]
    [InlineData("Name:\tdotnet\nUid:\t1001\t1001\t1001\t1001\nGid:\t127\t127\t127\t127\n", "1001:127")]
    [InlineData("Uid:\t1000\t1002\t1002\t1002\nGid:\t1000\t1003\t1003\t1003\n", "1002:1003")]
    [InlineData("Uid:\t0\t0\t0\t0\nGid:\t0\t0\t0\t0\n", "1000:1000")]
    [InlineData("Uid:\t1001\t1001\t1001\t1001\nGid:\t0\t0\t0\t0\n", "1000:1000")]
    [InlineData(null, "1000:1000")]
    [InlineData("garbage", "1000:1000")]
    public void The_container_user_is_the_owners_effective_uid_and_gid_and_never_root(string? procStatus, string expected)
    {
        DockerSandboxRunner.UserFor(procStatus).ShouldBe(expected);
    }

    [Fact]
    public async Task The_filesystem_is_read_only_but_the_work_folder_and_a_tmpfs_tmp()
    {
        await Run();

        foreach (var run in _processes.Runs)
        {
            run.Arguments.ShouldContain("--read-only");
            FakeProcessRunner.Value(run, "--tmpfs").ShouldBe("/tmp:rw,exec,nosuid,nodev");
            run.Arguments.Count(a => a == "--tmpfs").ShouldBe(1);
        }
    }

    [Fact]
    public async Task Every_capability_is_dropped_and_no_new_privileges_are_allowed()
    {
        await Run();

        foreach (var run in _processes.Runs)
        {
            FakeProcessRunner.HasPair(run, "--cap-drop", "ALL").ShouldBeTrue();
            FakeProcessRunner.HasPair(run, "--security-opt", "no-new-privileges").ShouldBeTrue();
            run.Arguments.ShouldNotContain("--cap-add");
            run.Arguments.ShouldNotContain("--privileged");
            run.Arguments.ShouldNotContain("--device");
        }
    }

    [Fact]
    public async Task The_pids_memory_and_cpu_caps_are_512_4g_and_2()
    {
        await Run();

        foreach (var run in _processes.Runs)
        {
            FakeProcessRunner.Value(run, "--pids-limit").ShouldBe("512");
            FakeProcessRunner.Value(run, "--memory").ShouldBe("4g");
            FakeProcessRunner.Value(run, "--cpus").ShouldBe("2");
        }
    }

    [Fact]
    public async Task The_container_gets_only_the_stacks_variables_and_the_test_run_its_offline_ones()
    {
        await Run();

        static string[] Variables(ProcessCall call) =>
            [.. call.Arguments.Zip(call.Arguments.Skip(1)).Where(pair => pair.First == "-e").Select(pair => pair.Second)];

        Variables(_processes.Restore).ShouldBe([
            "HOME=/work/cache/home",
            "npm_config_cache=/work/cache/npm",
            "npm_config_update_notifier=false",
            "COREPACK_HOME=/work/cache/corepack",
            "COREPACK_ENABLE_DOWNLOAD_PROMPT=0",
        ]);
        Variables(_processes.Test).ShouldBe([.. Variables(_processes.Restore), "npm_config_offline=true"]);
        foreach (var run in _processes.Runs)
        {
            run.Arguments.ShouldNotContain(a => a.StartsWith("--env", StringComparison.Ordinal));
            run.Arguments.Count(a => a == "-e").ShouldBe(Variables(run).Length);
        }
    }

    [Fact]
    public async Task No_variable_of_the_owners_environment_reaches_the_argv_or_the_docker_process()
    {
        await Run();

        _processes.Calls.Count.ShouldBe(3);
        foreach (var call in _processes.Calls)
        {
            call.Arguments.ShouldNotContain(a => a.Contains(FakeSecret, StringComparison.Ordinal) || a.Contains("SCOUT_FAKE_TOKEN", StringComparison.Ordinal) || a.Contains("GITHUB_TOKEN", StringComparison.Ordinal));
            call.Environment.Values.ShouldNotContain(FakeSecret);
            call.Environment.Keys.Order(StringComparer.Ordinal).ShouldBe(["DOCKER_HOST", "HOME", "PATH"]);
            call.Environment["HOME"].ShouldBe("/home/owner");
        }
    }

    [Fact]
    public async Task A_command_from_the_request_replaces_the_test_command_as_one_sh_c_word_on_the_offline_run()
    {
        var result = await Run(new SandboxRequest(Snapshot, Command: "  npx vitest run --reporter=dot; echo done  "));

        _processes.Restore.Arguments[^1].ShouldBe("npm ci");
        _processes.Test.Arguments.TakeLast(3).ShouldBe(["sh", "-c", "npx vitest run --reporter=dot; echo done"]);
        FakeProcessRunner.HasPair(_processes.Test, "--network", "none").ShouldBeTrue();
        result.Command.ShouldBe("npx vitest run --reporter=dot; echo done");
    }

    [Fact]
    public async Task A_dotnet_run_says_it_ran_on_the_net_10_sdk_with_roll_forward()
    {
        var dotnet = Directory.CreateDirectory(Path.Combine(_base, "dotnet")).FullName;
        File.WriteAllText(Path.Combine(dotnet, "App.csproj"), "<Project />");

        var result = await Run(new SandboxRequest(dotnet));

        result.Ran.ShouldBeTrue();
        result.Image.ShouldBe(SandboxImages.DotNet);
        result.Note.ShouldBe("Ran on the .NET 10 SDK with roll-forward.");
        _processes.Test.Arguments.ShouldContain("DOTNET_ROLL_FORWARD=Major");
    }

    [Fact]
    public async Task A_dotnet_restore_failure_keeps_the_note_and_a_missing_docker_has_none()
    {
        var dotnet = Directory.CreateDirectory(Path.Combine(_base, "dotnet")).FullName;
        File.WriteAllText(Path.Combine(dotnet, "App.csproj"), "<Project />");
        _processes.Behaviour = (call, _, _) => Task.FromResult(FakeProcessRunner.IsRun(call, "restore") ? 1 : 0);

        (await Run(new SandboxRequest(dotnet))).Note.ShouldBe("Ran on the .NET 10 SDK with roll-forward.");

        _processes.Behaviour = (_, _, _) => throw new Win32Exception(2, "No such file or directory");

        (await Run(new SandboxRequest(dotnet))).Note.ShouldBeNull();
    }

    [Fact]
    public async Task The_result_carries_the_exit_code_and_how_long_the_run_took()
    {
        _processes.Behaviour = (call, _, _) =>
        {
            if (FakeProcessRunner.IsRun(call, "test"))
            {
                _time.Advance(TimeSpan.FromSeconds(12.34));
                return Task.FromResult(3);
            }

            return Task.FromResult(0);
        };

        var result = await Run();

        result.Ran.ShouldBeTrue();
        result.ExitCode.ShouldBe(3);
        result.DurationSeconds.ShouldBe(12.3);
    }

    [Fact]
    public async Task The_output_is_cut_to_its_last_200000_characters_in_the_log_and_its_last_4000_in_the_result()
    {
        _processes.Behaviour = (call, onOutput, _) =>
        {
            if (FakeProcessRunner.IsRun(call, "test"))
            {
                onOutput("FIRST-CHUNK");
                for (var i = 0; i < 30; i++)
                {
                    onOutput(new string('x', 10_000));
                }

                onOutput("LAST-LINE");
            }

            return Task.FromResult(0);
        };

        var result = await Run();

        var log = File.ReadAllText(Directory.GetFiles(WorkRoot, "*.log").Single());
        log.Length.ShouldBe(200_000);
        log.ShouldNotContain("FIRST-CHUNK");
        log.ShouldEndWith("x" + "LAST-LINE");
        result.Output.Length.ShouldBe(4_000);
        result.Output.ShouldEndWith("LAST-LINE");
    }

    [Fact]
    public async Task Docker_missing_from_the_path_runs_nothing_and_says_docker_is_not_installed()
    {
        _processes.Behaviour = (_, _, _) => throw new Win32Exception(2, "No such file or directory");

        SandboxResult? outcome = null;
        await Should.NotThrowAsync(async () => outcome = await Run());
        var result = outcome.ShouldNotBeNull();

        result.ShouldBe(new SandboxResult(false, "Docker is not installed or not running.", null, null, null, 0, string.Empty));
        _processes.Calls.Count.ShouldBe(1);
        Directory.Exists(WorkRoot).ShouldBeFalse();
    }

    [Fact]
    public async Task A_docker_daemon_that_does_not_answer_runs_nothing_and_says_so()
    {
        _processes.Behaviour = (call, _, _) => Task.FromResult(call.Arguments[0] == "version" ? 1 : 0);

        var result = await Run();

        result.ShouldBe(new SandboxResult(false, "Docker is not installed or not running.", null, null, null, 0, string.Empty));
        _processes.Runs.ShouldBeEmpty();
        Directory.Exists(WorkRoot).ShouldBeFalse();
    }

    [Fact]
    public async Task A_restore_that_exits_non_zero_reports_its_exit_and_output_and_runs_no_tests()
    {
        _processes.Behaviour = (call, onOutput, _) =>
        {
            if (FakeProcessRunner.IsRun(call, "restore"))
            {
                onOutput("npm ERR! 404 Not Found - left-pad\n");
                return Task.FromResult(1);
            }

            return Task.FromResult(0);
        };

        var result = await Run();

        result.Ran.ShouldBeTrue();
        result.ExitCode.ShouldBe(1);
        result.Command.ShouldBe("npm ci");
        result.Reason.ShouldBe("The restore exited with code 1; the tests did not run.");
        result.Output.ShouldContain("npm ERR! 404 Not Found - left-pad");
        _processes.Runs.Count.ShouldBe(1);
        RunFoldersLeft().ShouldBeEmpty();
    }

    [Fact]
    public async Task A_run_past_20_minutes_is_killed_by_its_container_name_and_says_so()
    {
        var hanging = new TaskCompletionSource<CancellationToken>();
        _processes.Behaviour = async (call, _, ct) =>
        {
            if (FakeProcessRunner.IsRun(call, "test"))
            {
                hanging.SetResult(ct);
                await Task.Delay(Timeout.Infinite, ct);
            }

            return 0;
        };

        var run = Run();
        var testRun = await hanging.Task.WaitAsync(TimeSpan.FromSeconds(10));
        _time.Advance(TimeSpan.FromMinutes(20) - TimeSpan.FromSeconds(1));
        testRun.IsCancellationRequested.ShouldBeFalse("the run was stopped before 20 minutes");
        _time.Advance(TimeSpan.FromSeconds(1));
        testRun.IsCancellationRequested.ShouldBeTrue("the run was not stopped at 20 minutes");
        (await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(10)))).ShouldBe(run, "the run did not end at 20 minutes");
        var result = await run;

        result.Ran.ShouldBeFalse();
        result.Reason.ShouldBe("The tests ran past the 20-minute limit.");
        result.Image.ShouldBe(SandboxImages.Node);
        result.Command.ShouldBe("npm test");
        _processes.Calls[^1].Arguments.ShouldBe(["kill", FakeProcessRunner.Name(_processes.Test)]);
        RunFoldersLeft().ShouldBeEmpty();
    }

    [Fact]
    public async Task A_cancelled_run_kills_its_container_deletes_the_work_folder_and_throws()
    {
        using var cancel = new CancellationTokenSource();
        _processes.Behaviour = async (call, _, ct) =>
        {
            if (FakeProcessRunner.IsRun(call, "restore"))
            {
                await cancel.CancelAsync();
                await Task.Delay(Timeout.Infinite, ct);
            }

            return 0;
        };

        await Should.ThrowAsync<OperationCanceledException>(() => Run(ct: cancel.Token));

        _processes.Calls[^1].Arguments.ShouldBe(["kill", FakeProcessRunner.Name(_processes.Restore)]);
        RunFoldersLeft().ShouldBeEmpty();
    }

    [Fact]
    public async Task A_snapshot_without_a_known_stack_starts_no_process()
    {
        File.Delete(Path.Combine(Snapshot, "package.json"));

        var result = await Run();

        result.ShouldBe(new SandboxResult(false, "No .NET, Node, Python or Go project was found at the repository's root.", null, null, null, 0, string.Empty));
        _processes.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_missing_snapshot_starts_no_process()
    {
        var result = await Run(new SandboxRequest(Path.Combine(_base, "absent")));

        result.ShouldBe(new SandboxResult(false, "The repository snapshot was not found.", null, null, null, 0, string.Empty));
        _processes.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task With_the_real_process_runner_a_docker_path_that_does_not_exist_says_docker_is_not_installed()
    {
        var runner = new DockerSandboxRunner(
            new SandboxOptions { WorkDirectory = WorkRoot, DockerPath = Path.Combine(_base, "no-docker-here") },
            _time,
            SystemProcessRunner.Instance,
            _parentEnvironment);

        SandboxResult? outcome = null;
        await Should.NotThrowAsync(async () => outcome = await runner.RunTestsAsync(new SandboxRequest(Snapshot), CancellationToken.None));
        var result = outcome.ShouldNotBeNull();

        result.ShouldBe(new SandboxResult(false, "Docker is not installed or not running.", null, null, null, 0, string.Empty));
        Directory.Exists(WorkRoot).ShouldBeFalse();
    }

    [Fact]
    public async Task A_sandbox_folder_inside_the_snapshot_is_refused_before_any_copy()
    {
        var runner = new DockerSandboxRunner(
            new SandboxOptions { WorkDirectory = Path.Combine(Snapshot, "work") },
            _time,
            _processes,
            _parentEnvironment);

        var result = await runner.RunTestsAsync(new SandboxRequest(Snapshot + Path.DirectorySeparatorChar), CancellationToken.None);

        result.ShouldBe(new SandboxResult(false, "The sandbox folder is inside the repository snapshot, so the copy would never end.", null, null, null, 0, string.Empty));
        _processes.Calls.ShouldBeEmpty();
        Directory.Exists(Path.Combine(Snapshot, "work")).ShouldBeFalse();
    }

    [Fact]
    public async Task A_sandbox_folder_beside_the_snapshot_with_the_same_prefix_is_not_refused()
    {
        var runner = new DockerSandboxRunner(
            new SandboxOptions { WorkDirectory = Snapshot + "-work" },
            _time,
            _processes,
            _parentEnvironment);

        var result = await runner.RunTestsAsync(new SandboxRequest(Snapshot), CancellationToken.None);

        result.Ran.ShouldBeTrue();
        _processes.Runs.Count.ShouldBe(2);
    }
}
