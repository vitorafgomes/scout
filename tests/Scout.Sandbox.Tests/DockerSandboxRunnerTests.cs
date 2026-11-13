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
        result.ShouldBe(new SandboxResult(true, null, SandboxImages.Node, "npm test", 0, 0, "$ npm ci\n$ npm test\n", "No Node version was declared; ran on Node 22.23.4."));
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
    public async Task The_work_folder_is_deleted_after_the_run_and_only_the_log_and_its_record_stay_under_the_root()
    {
        await Run();

        var source = FakeProcessRunner.MountSource(_processes.Test);
        Directory.Exists(source).ShouldBeFalse();
        RunFoldersLeft().ShouldBeEmpty();
        Directory.GetFiles(WorkRoot).Select(Path.GetFileName).Order().ShouldBe([Path.GetFileName(source) + ".log", Path.GetFileName(source) + ".run.json"]);
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
        result.Note.ShouldBe("No SDK was declared; ran on the .NET 10 SDK.");
        _processes.Test.Arguments.ShouldContain("DOTNET_ROLL_FORWARD=Major");
    }

    private string DotNetWithDockerTests()
    {
        var dotnet = Directory.CreateDirectory(Path.Combine(_base, "dotnet")).FullName;
        File.WriteAllText(Path.Combine(dotnet, "App.csproj"), "<Project />");
        File.WriteAllText(Path.Combine(dotnet, "DockerTests.cs"), "[Trait(\"Category\", \"Docker\")] public class DockerTests { }");
        return dotnet;
    }

    [Fact]
    public async Task The_note_says_which_tests_were_left_out()
    {
        var result = await Run(new SandboxRequest(DotNetWithDockerTests()));

        _processes.Test.Arguments[^1].ShouldBe("dotnet test 'App.csproj' --no-restore --filter 'Category!=Docker'");
        result.Command.ShouldBe("dotnet test 'App.csproj' --no-restore --filter 'Category!=Docker'");
        result.Note.ShouldBe("No SDK was declared; ran on the .NET 10 SDK. Left out the tests marked Category=Docker, found in 1 file(s): the container has no Docker daemon.");
    }

    [Fact]
    public async Task A_callers_command_leaves_nothing_out_and_the_note_does_not_say_so()
    {
        var result = await Run(new SandboxRequest(DotNetWithDockerTests(), Command: "dotnet test 'App.csproj' --no-restore"));

        _processes.Test.Arguments[^1].ShouldBe("dotnet test 'App.csproj' --no-restore");
        result.Command.ShouldBe("dotnet test 'App.csproj' --no-restore");
        result.Note.ShouldBe("No SDK was declared; ran on the .NET 10 SDK.");
    }

    [Fact]
    public async Task A_dotnet_restore_failure_keeps_the_note_and_a_missing_docker_has_none()
    {
        var dotnet = Directory.CreateDirectory(Path.Combine(_base, "dotnet")).FullName;
        File.WriteAllText(Path.Combine(dotnet, "App.csproj"), "<Project />");
        _processes.Behaviour = (call, _, _) => Task.FromResult(FakeProcessRunner.IsRun(call, "restore") ? 1 : 0);

        (await Run(new SandboxRequest(dotnet))).Note.ShouldBe("No SDK was declared; ran on the .NET 10 SDK.");

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

    [Theory]
    [InlineData("docker: Error response from daemon: invalid mount config for type \"bind\": bind source path does not exist: {0}\n")]
    [InlineData("docker: Error response from daemon: Mounts denied: \nThe path {0} is not shared from the host and is not known to Docker.\n")]
    public async Task A_work_folder_docker_cannot_see_says_where_to_share_it_never_the_daemon_text(string daemonError)
    {
        _processes.Behaviour = (call, onOutput, _) =>
        {
            if (FakeProcessRunner.IsRun(call, "restore"))
            {
                onOutput(string.Format(System.Globalization.CultureInfo.InvariantCulture, daemonError, FakeProcessRunner.MountSource(call)));
                return Task.FromResult(125);
            }

            return Task.FromResult(0);
        };

        var result = await Run();

        result.Ran.ShouldBeFalse();
        result.Reason.ShouldBe($"Docker cannot see Scout's work folder {WorkRoot}; with Docker Desktop, add it under Settings, Resources, File sharing.");
        result.Image.ShouldBeNull();
        result.Command.ShouldBeNull();
        result.ExitCode.ShouldBeNull();
        _processes.Runs.Count.ShouldBe(1);
        RunFoldersLeft().ShouldBeEmpty();
    }

    [Fact]
    public async Task A_restore_whose_own_output_mentions_a_bind_source_but_exits_1_is_a_failed_restore()
    {
        _processes.Behaviour = (call, onOutput, _) =>
        {
            if (FakeProcessRunner.IsRun(call, "restore"))
            {
                onOutput("[ERROR] bind source path does not exist\n");
                return Task.FromResult(1);
            }

            return Task.FromResult(0);
        };

        var result = await Run();

        result.Ran.ShouldBeTrue();
        result.Reason.ShouldBe("The restore exited with code 1; the tests did not run.");
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
    public async Task A_run_the_owner_stops_during_the_tests_kills_its_self_removing_container_with_a_token_of_its_own_and_keeps_no_run_record()
    {
        using var stop = new CancellationTokenSource();
        var killToken = CancellationToken.None;
        _processes.Behaviour = async (call, _, ct) =>
        {
            if (FakeProcessRunner.IsRun(call, "test"))
            {
                await stop.CancelAsync();
                await Task.Delay(Timeout.Infinite, ct);
            }

            if (call.Arguments[0] == "kill")
            {
                killToken = ct;
            }

            return 0;
        };

        await Should.ThrowAsync<OperationCanceledException>(() => Run(ct: stop.Token));

        _processes.Calls[^1].Arguments.ShouldBe(["kill", FakeProcessRunner.Name(_processes.Test)]);
        _processes.Test.Arguments.ShouldContain("--rm", "docker removes the killed container itself");
        killToken.IsCancellationRequested.ShouldBeFalse("the kill does not run on the stopped run's token");
        RunFoldersLeft().ShouldBeEmpty();
        Directory.GetFiles(WorkRoot, "*.run.json").ShouldBeEmpty("a stopped run is no result, so no run record is written");
    }

    [Fact]
    public async Task A_snapshot_without_a_known_stack_starts_no_process()
    {
        File.Delete(Path.Combine(Snapshot, "package.json"));

        var result = await Run();

        result.ShouldBe(new SandboxResult(false, "No .NET, Node, Python, Go or Java project was found at the repository's root.", null, null, null, 0, string.Empty));
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

    [Theory]
    [InlineData(127, "The test command could not be started (exit 127: a command it names was not found or is not executable).")]
    [InlineData(126, "The test command could not be started (exit 126: a command it names was not found or is not executable).")]
    [InlineData(2, null)]
    [InlineData(1, null)]
    public async Task A_test_command_the_shell_could_not_start_says_so_apart_from_tests_that_failed(int exit, string? reason)
    {
        _processes.Behaviour = (call, onOutput, _) =>
        {
            if (FakeProcessRunner.IsRun(call, "test"))
            {
                onOutput("sh: 1: jest: not found\n");
                return Task.FromResult(exit);
            }

            return Task.FromResult(0);
        };

        var result = await Run();

        (result.Ran, result.ExitCode, result.Command, result.Reason).ShouldBe((true, (int?)exit, "npm test", reason));
    }

    private const string RunStart = "$ npm ci\n$ npm test\n";

    private void TestsPrint(string output, int exit = 1) =>
        _processes.Behaviour = (call, onOutput, _) =>
        {
            if (FakeProcessRunner.IsRun(call, "test"))
            {
                foreach (var chunk in output.Chunk(1_000))
                {
                    onOutput(new string(chunk));
                }

                return Task.FromResult(exit);
            }

            return Task.FromResult(0);
        };

    private static string PassingSummaries(int count) =>
        string.Concat(Enumerable.Range(1, count).Select(i => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"Passed!  - Failed:     0, Passed:    {i,3}, Skipped:     0, Total:    {i,3}, Duration: 1 s - Project{i:D3}.Tests.dll (net10.0)\n")));

    [Fact]
    public async Task A_failing_run_returns_the_lines_that_name_the_failure_and_the_end_of_the_output()
    {
        var failingBlock = """
            [xUnit.net 00:00:01.20]     Orders.Tests.CheckoutTests.Checkout_applies_the_discount [FAIL]
              Failed Orders.Tests.CheckoutTests.Checkout_applies_the_discount [12 ms]
              Error Message:
               Assert.Equal() Failure: Values differ
            Expected: 90
            Actual:   100
              Stack Trace:
                 at Orders.Tests.CheckoutTests.Checkout_applies_the_discount() in /work/src/Orders.Tests/CheckoutTests.cs:line 21
            /work/src/Orders/Legacy.cs(3,1): error CS0000: The legacy step failed
            Failed!  - Failed:     1, Passed:    41, Skipped:     0, Total:    42, Duration: 2 s - Orders.Tests.dll (net10.0)

            """;
        var output = PassingSummaries(20) + failingBlock + PassingSummaries(120);
        TestsPrint(output);

        var result = await Run();

        output.Length.ShouldBeGreaterThan(10_000);
        result.ExitCode.ShouldBe(1);
        result.Output.ShouldContain("Orders.Tests.CheckoutTests.Checkout_applies_the_discount [FAIL]");
        result.Output.ShouldContain("Assert.Equal() Failure: Values differ");
        result.Output.ShouldContain("/work/src/Orders/Legacy.cs(3,1): error CS0000: The legacy step failed");
        result.Output.ShouldContain("Failed!  - Failed:     1, Passed:    41");
        result.Output.ShouldNotContain("Stack Trace:");
        result.Output.ShouldStartWith("Failure lines found earlier in the output:\n");
        result.Output.ShouldEndWith($"End of the output:\n{(RunStart + output)[^4_000..]}");
        result.Output.Length.ShouldBeLessThanOrEqualTo(DockerSandboxRunner.FailedOutputLimit);
    }

    [Fact]
    public async Task Failure_lines_beyond_their_bound_keep_the_first_ones_and_say_how_many_were_left_out()
    {
        var failures = string.Concat(Enumerable.Range(1, 100).Select(i => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"  Failed Some.Namespace.Test_{i:D3} [1 ms]\n")));
        TestsPrint(failures + PassingSummaries(60));

        var result = await Run();

        result.Output.ShouldContain("  Failed Some.Namespace.Test_050 [1 ms]\n");
        result.Output.ShouldNotContain("Test_051");
        result.Output.ShouldContain("(50 more failure lines left out)");
        result.Output.Length.ShouldBeLessThanOrEqualTo(DockerSandboxRunner.FailedOutputLimit);
    }

    [Fact]
    public async Task A_failing_run_without_a_failure_line_returns_only_the_end_of_the_output()
    {
        var output = string.Concat(Enumerable.Range(1, 200).Select(i => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"step {i:D3} of the build ran\n")));
        TestsPrint(output);

        var result = await Run();

        result.Output.ShouldBe((RunStart + output)[^4_000..]);
    }

    [Fact]
    public async Task A_failing_run_whose_output_fits_the_bound_is_returned_whole_without_a_line_twice()
    {
        var output = "  Failed Some.Namespace.Test_001 [1 ms]\n  Error Message:\n   expected 1 but was 2\nFailed!  - Failed:     1, Passed:     3\n";
        TestsPrint(output);

        var result = await Run();

        result.Output.ShouldBe(RunStart + output);
    }

    [Fact]
    public async Task A_run_past_20_minutes_returns_the_failure_lines_printed_before_it_was_killed()
    {
        var printed = new TaskCompletionSource<CancellationToken>();
        _processes.Behaviour = async (call, onOutput, ct) =>
        {
            if (FakeProcessRunner.IsRun(call, "test"))
            {
                onOutput("FAIL src/orders/checkout.test.js\n");
                onOutput(PassingSummaries(60));
                printed.SetResult(ct);
                await Task.Delay(Timeout.Infinite, ct);
            }

            return 0;
        };

        var run = Run();
        await printed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        _time.Advance(TimeSpan.FromMinutes(20));
        var result = await run.WaitAsync(TimeSpan.FromSeconds(10));

        result.Reason.ShouldBe("The tests ran past the 20-minute limit.");
        result.Output.ShouldStartWith("Failure lines found earlier in the output:\nFAIL src/orders/checkout.test.js\n");
        result.Output.ShouldEndWith(PassingSummaries(60)[^4_000..]);
    }

    [Fact]
    public async Task A_passing_run_that_printed_a_failure_like_line_returns_only_the_end_of_the_output()
    {
        var output = "  Failed Orders.Tests.CheckoutTests.Flaky_then_retried [12 ms]\n" + PassingSummaries(60);
        TestsPrint(output, exit: 0);

        var result = await Run();

        result.ExitCode.ShouldBe(0);
        result.Output.ShouldBe((RunStart + output)[^4_000..]);
    }

    [Fact]
    public async Task A_failed_restore_leads_with_its_failure_lines_and_ends_with_the_end_of_the_output()
    {
        const string NotFound = "/work/src/App/App.csproj : error NU1101: Unable to find package Left.Pad. No packages exist with this id in source(s): nuget.org\n";
        var printed = NotFound + PassingSummaries(60);
        _processes.Behaviour = (call, onOutput, _) =>
        {
            if (FakeProcessRunner.IsRun(call, "restore"))
            {
                onOutput(printed);
                return Task.FromResult(1);
            }

            return Task.FromResult(0);
        };

        var result = await Run();

        result.Reason.ShouldBe("The restore exited with code 1; the tests did not run.");
        result.Output.ShouldStartWith("Failure lines found earlier in the output:\n" + NotFound);
        result.Output.ShouldEndWith("\nEnd of the output:\n" + ("$ npm ci\n" + printed)[^4_000..]);
    }

    [Theory]
    [InlineData("  Failed FixtureTest.Checkout_applies_the_discount [1 ms]\n")]
    [InlineData("failed FixtureTest.Checkout_applies_the_discount (2ms)\n  Assert.Equal() Failure: Values differ\nExpected: 90\n")]
    [InlineData("Test run summary: Failed!\n")]
    [InlineData("FAILED tests/test_checkout.py::test_applies_the_discount - assert 100 == 90\n")]
    [InlineData("E       assert 100 == 90\n")]
    [InlineData("[ERROR] Tests run: 2, Failures: 1, Errors: 0, Skipped: 0, Time elapsed: 0.041 s <<< FAILURE! -- in fixture.FixtureTest\n")]
    [InlineData("[ERROR] fixture.FixtureTest.appliesTheDiscount -- Time elapsed: 0.012 s <<< FAILURE!\n")]
    [InlineData("org.opentest4j.AssertionFailedError: expected: <90> but was: <100>\n")]
    [InlineData("FixtureTest > appliesTheDiscount() FAILED\n")]
    [InlineData("test tests::applies_the_discount ... FAILED\n")]
    [InlineData("thread 'tests::applies_the_discount' panicked at src/lib.rs:12:9:\n")]
    [InlineData("  \u25CF Checkout \u203A applies the discount\n")]
    public async Task The_line_each_test_runner_prints_for_a_failure_is_kept_before_the_end_of_the_output(string printed)
    {
        TestsPrint(printed + PassingSummaries(60));

        var result = await Run();

        result.Output.ShouldStartWith("Failure lines found earlier in the output:\n" + printed);
    }
}
