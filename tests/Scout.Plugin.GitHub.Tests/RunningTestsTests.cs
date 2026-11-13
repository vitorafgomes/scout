using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using Microsoft.SemanticKernel;
using Scout.Plugin.GitHub.Abstractions;
using Scout.Plugin.GitHub.Tests.Fakes;

namespace Scout.Plugin.GitHub.Tests;

/// <summary>
/// The owner stops a pull request's test run from Scout Desktop: the run's token is cancelled, the turn that asked for the
/// tests ends with a sentence that the owner stopped it, and nothing is kept as the pull request's tests; each run ends one way.
/// </summary>
public sealed class RunningTestsTests : IDisposable
{
    private static readonly RepositoryCommit Head = new("abc1234def5678", "abc1234def5678", IsDefaultBranch: false);

    private static readonly SandboxRunResult Passed = new(true, null, "mcr.microsoft.com/dotnet/sdk@sha256:abc", "dotnet test", 0, 42, "Passed! 12 tests");

    private readonly string _dir = Directory.CreateTempSubdirectory("scout-github-stop-tests-").FullName;
    private readonly IGitHubClient _reads = Substitute.For<IGitHubClient>();
    private readonly ISandboxRunner _sandbox = Substitute.For<ISandboxRunner>();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 10, 10, 0, 0, TimeSpan.Zero));
    private readonly TestRuns _kept = new();
    private readonly RunningTests _running = new();

    public RunningTestsTests()
    {
        _reads.GetPullRequestHeadAsync("octocat", "hello", 42, Arg.Any<CancellationToken>()).Returns(new PullRequestHead(Head.Sha, "alice"));
        _reads.ResolveCommitAsync("octocat", "hello", Head.Sha, Arg.Any<CancellationToken>()).Returns(Head);
        _reads.DownloadTarballAsync("octocat", "hello", Head.Sha, Arg.Any<Func<Stream, CancellationToken, Task>>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                await call.ArgAt<Func<Stream, CancellationToken, Task>>(3)(new MemoryStream(TarGzFixture.Build(TarGzFixture.Text("src/A.cs", "class A { }\n"))), CancellationToken.None);
                return true;
            });
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private Task<string> RunAsync() => new GitHubActionsPlugin(
            _reads, Substitute.For<IGitHubWriteClient>(), new GitHubProfileStore(Path.Combine(_dir, "profiles")),
            new RepositorySnapshotStore(Path.Combine(_dir, "snapshots"), 0, _time), Substitute.For<IRepositoryProjects>(), _time,
            null, _sandbox, _kept, null, null, _running)
        .RunPullRequestTestsAsync("octocat", "hello", 42, CancellationToken.None);

    private TaskCompletionSource<CancellationToken> RunsUntilCancelled()
    {
        var started = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        _sandbox.RunTestsAsync(Arg.Any<SandboxRunRequest>(), Arg.Any<CancellationToken>()).Returns(async call =>
        {
            var token = call.ArgAt<CancellationToken>(1);
            started.TrySetResult(token);
            await Task.Delay(Timeout.Infinite, token);
            return Passed;
        });
        return started;
    }

    private static (bool Ran, string? Reason) Outcome(string answer)
    {
        var root = JsonDocument.Parse(answer).RootElement;
        return (root.GetProperty("ran").GetBoolean(), root.TryGetProperty("reason", out var reason) ? reason.GetString() : null);
    }

    [Fact]
    public async Task Stopping_a_running_run_cancels_it_ends_the_turn_saying_the_owner_stopped_it_and_keeps_no_result()
    {
        var started = RunsUntilCancelled();
        var answer = RunAsync();
        var token = await started.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var stopped = await _running.StopAsync("octocat", "hello", 42, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));

        token.IsCancellationRequested.ShouldBeTrue("the run's token reaches the sandbox, and the stop cancels it");
        stopped.ShouldBe([new StoppedRun(null, Stopped: true)], "one run was cut short, asked outside a chat in this test");
        Outcome(await answer.WaitAsync(TimeSpan.FromSeconds(10))).ShouldBe((false, GitHubActionsPlugin.StoppedByOwner));
        _kept.Find("octocat", "hello", Head.Sha[..7]).ShouldBeNull();
    }

    [Fact]
    public async Task Stopping_while_the_snapshot_downloads_ends_the_run_before_any_container_starts_and_leaves_no_snapshot()
    {
        var downloading = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        _reads.DownloadTarballAsync("octocat", "hello", Head.Sha, Arg.Any<Func<Stream, CancellationToken, Task>>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                var token = call.ArgAt<CancellationToken>(4);
                downloading.TrySetResult(token);
                await Task.Delay(Timeout.Infinite, token);
                return true;
            });
        var answer = RunAsync();
        await downloading.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var stopped = await _running.StopAsync("octocat", "hello", 42, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));

        stopped.ShouldBe([new StoppedRun(null, Stopped: true)], "the run is stoppable from the moment the function starts, not only once the container runs");
        Outcome(await answer.WaitAsync(TimeSpan.FromSeconds(10))).ShouldBe((false, GitHubActionsPlugin.StoppedByOwner));
        await _sandbox.DidNotReceiveWithAnyArgs().RunTestsAsync(default!, default);
        _kept.Find("octocat", "hello", Head.Sha[..7]).ShouldBeNull();
        var snapshots = Path.Combine(_dir, "snapshots");
        (Directory.Exists(snapshots) ? Directory.GetFiles(snapshots, "*", SearchOption.AllDirectories) : []).ShouldBeEmpty("a snapshot cut short is deleted");
    }

    [Fact]
    public async Task Stopping_when_no_run_of_that_pull_request_is_in_progress_stops_nothing()
    {
        using var other = _running.Start("octocat", "hello", 41, "local:session", CancellationToken.None);

        (await _running.StopAsync("octocat", "hello", 42, CancellationToken.None)).ShouldBeEmpty();
        other.Token.IsCancellationRequested.ShouldBeFalse("a run of another pull request is not stopped");
    }

    [Fact]
    public async Task Stopping_twice_asks_the_run_once_and_names_its_conversation()
    {
        var run = _running.Start("Octocat", "Hello", 42, "local:session", CancellationToken.None);

        var first = _running.StopAsync("octocat", "hello", 42, CancellationToken.None);
        var second = await _running.StopAsync("octocat", "hello", 42, CancellationToken.None);
        run.Token.IsCancellationRequested.ShouldBeTrue();
        run.End(stopped: true);
        run.Dispose();

        (await first.WaitAsync(TimeSpan.FromSeconds(10))).ShouldBe([new StoppedRun("local:session", Stopped: true)]);
        second.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_stop_that_lands_as_the_sandbox_returns_its_result_lets_the_finished_run_stand_everywhere()
    {
        Task<IReadOnlyList<StoppedRun>>? stopping = null;
        _sandbox.RunTestsAsync(Arg.Any<SandboxRunRequest>(), Arg.Any<CancellationToken>()).Returns(_ =>
        {
            stopping = _running.StopAsync("octocat", "hello", 42, CancellationToken.None);
            return Task.FromResult(Passed);
        });

        var answer = await RunAsync();
        var stopped = await stopping!.WaitAsync(TimeSpan.FromSeconds(10));

        Outcome(answer).ShouldBe((true, null), "the sandbox returned, so it has written the run's record: the result stands");
        _kept.Find("octocat", "hello", Head.Sha[..7]).ShouldNotBeNull().ExitCode.ShouldBe(0);
        stopped.ShouldBe([new StoppedRun(null, Stopped: false)], "the stop is told the run had already finished");
    }

    [Fact]
    public async Task A_run_that_fails_another_way_once_the_stop_was_asked_ends_as_stopped_with_no_result()
    {
        var downloading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _reads.DownloadTarballAsync("octocat", "hello", Head.Sha, Arg.Any<Func<Stream, CancellationToken, Task>>(), Arg.Any<CancellationToken>())
            .Returns<Task<bool>>(async call =>
            {
                var token = call.ArgAt<CancellationToken>(4);
                downloading.TrySetResult();
                await Task.Delay(Timeout.Infinite, token).ContinueWith(_ => { }, TaskScheduler.Default);
                throw new IOException("the half-written snapshot could not be read");
            });
        var answer = RunAsync();
        await downloading.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var stopped = await _running.StopAsync("octocat", "hello", 42, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));

        stopped.ShouldBe([new StoppedRun(null, Stopped: true)], "a run that ended in any failure after the stop was asked was cut short, not finished");
        Outcome(await answer.WaitAsync(TimeSpan.FromSeconds(10))).ShouldBe((false, GitHubActionsPlugin.StoppedByOwner));
        _kept.Find("octocat", "hello", Head.Sha[..7]).ShouldBeNull();
    }

    [Fact]
    public async Task A_run_disposed_after_the_stop_was_asked_without_saying_how_it_ended_counts_as_cut_short()
    {
        var run = _running.Start("octocat", "hello", 42, "local:session", CancellationToken.None);

        var stopping = _running.StopAsync("octocat", "hello", 42, CancellationToken.None);
        run.Dispose();

        (await stopping.WaitAsync(TimeSpan.FromSeconds(10))).ShouldBe([new StoppedRun("local:session", Stopped: true)]);
    }

    [Fact]
    public async Task A_run_that_finished_before_the_stop_keeps_its_result_and_the_stop_finds_nothing()
    {
        _sandbox.RunTestsAsync(Arg.Any<SandboxRunRequest>(), Arg.Any<CancellationToken>()).Returns(Passed);

        var answer = await RunAsync();

        Outcome(answer).ShouldBe((true, null));
        (await _running.StopAsync("octocat", "hello", 42, CancellationToken.None)).ShouldBeEmpty();
        _kept.Find("octocat", "hello", Head.Sha[..7]).ShouldNotBeNull().ExitCode.ShouldBe(0);
    }

    [Fact]
    public async Task Cancelling_the_kernel_invocation_reaches_the_sandbox_token_through_semantic_kernel()
    {
        var started = RunsUntilCancelled();
        var plugin = KernelPluginFactory.CreateFromObject(new GitHubActionsPlugin(
            _reads, Substitute.For<IGitHubWriteClient>(), new GitHubProfileStore(Path.Combine(_dir, "profiles")),
            new RepositorySnapshotStore(Path.Combine(_dir, "snapshots"), 0, _time), Substitute.For<IRepositoryProjects>(), _time,
            null, _sandbox, _kept, null, null, _running), "GitHubActions");
        var kernel = new Kernel();
        kernel.Plugins.Add(plugin);
        using var turn = new CancellationTokenSource();

        var invocation = kernel.InvokeAsync(plugin["run_pull_request_tests"], new KernelArguments { ["owner"] = "octocat", ["repo"] = "hello", ["number"] = 42 }, turn.Token);
        var token = await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await turn.CancelAsync();

        token.IsCancellationRequested.ShouldBeTrue("the token Semantic Kernel binds to the function's CancellationToken is the invocation's, and the run's token is linked to it");
        await Should.ThrowAsync<OperationCanceledException>(() => invocation.WaitAsync(TimeSpan.FromSeconds(10)));
    }
}
