using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Scout.ControlPlane.Actions;
using Scout.ControlPlane.Features;
using Scout.ControlPlane.WriteGate;
using Scout.Llm;
using Scout.Llm.Abstractions;
using Scout.Plugin.GitHub;
using Shouldly;
using Xunit;

namespace Scout.Host.Console.Tests;

/// <summary>
/// Ten issues one request queued, approved with one decision, then sent by the worker's real executor over Scout's own
/// queue, log and feature state in a temp folder and a fake GitHub: each one is still claimed, sent at most once, paced,
/// recorded and failed on its own.
/// </summary>
public sealed class GitHubSetApprovalTests : IDisposable
{
    private const string Group = "turn-1";

    private readonly string _directory = Directory.CreateTempSubdirectory("scout-set-approval-").FullName;
    private readonly JsonFileFeatureStateStore _states;
    private readonly JsonFileActionQueue _actions;
    private readonly JsonLinesWriteLog _writes;
    private readonly WriteGateService _gate;
    private readonly FakeGitHub _github = new();

    public GitHubSetApprovalTests()
    {
        var options = Options.Create(new FeatureStateStoreOptions { FilePath = Path.Combine(_directory, "features.json") });
        _states = new JsonFileFeatureStateStore(options, NullLogger<JsonFileFeatureStateStore>.Instance);
        _actions = new JsonFileActionQueue(options, NullLogger<JsonFileActionQueue>.Instance);
        _writes = new JsonLinesWriteLog(options, NullLogger<JsonLinesWriteLog>.Instance);
        _gate = new WriteGateService(new FeatureCatalog(), _states, _actions, _writes);
    }

    public void Dispose()
    {
        _states.Dispose();
        _actions.Dispose();
        _writes.Dispose();
        _github.Dispose();
        Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public async Task A_Worker_That_Stops_Before_Recording_The_Third_Of_Ten_Leaves_Seven_Sent_One_Not_Confirmed_None_Twice()
    {
        await using var provider = await ComposeAsync();
        var ids = await ApproveTenAsync();
        var gate = new InProcessGate(_gate);
        var first = Executor(provider, gate, TimeProvider.System);
        await first.RunOnceAsync(CancellationToken.None);
        await first.RunOnceAsync(CancellationToken.None);
        gate.RecordFailure = new IOException("The worker stopped.");
        await first.RunOnceAsync(CancellationToken.None);
        gate.RecordFailure = null;
        var sentByTheFirst = _github.Titles.Count;
        await LapseClaimAsync(ids[2]);

        var second = Executor(provider, gate, TimeProvider.System);
        var runs = new List<GitHubActionExecutor.Ran>();
        do
        {
            runs.Add(await second.RunOnceAsync(CancellationToken.None));
        }
        while (runs[^1] != GitHubActionExecutor.Ran.Nothing && runs.Count < 20);

        sentByTheFirst.ShouldBe(3);
        runs.ShouldBe([.. Enumerable.Repeat(GitHubActionExecutor.Ran.Sent, 7), GitHubActionExecutor.Ran.Nothing]);
        (_github.Titles.Count - sentByTheFirst).ShouldBe(7, "the second worker sends the seven the first never took");
        _github.Titles.ShouldBe(Enumerable.Range(0, 10).Select(i => $"Finding {i}"), ignoreOrder: true);
        var ended = await OutcomesAsync(ids);
        ended[2].ShouldBe((PendingActionStatus.Executed, WriteGateService.NotConfirmedOutcome));
        ended.Where((_, i) => i != 2).ShouldAllBe(e => e.Status == PendingActionStatus.Executed && e.Outcome == "succeeded");
    }

    [Fact]
    public async Task The_Fifth_Of_Ten_Answered_502_Fails_Alone_And_The_Rest_Go_One_Second_Apart()
    {
        await using var provider = await ComposeAsync();
        var ids = await ApproveTenAsync();
        var clock = new SignallingClock();
        var started = clock.GetUtcNow();
        _github.Clock = clock;
        _github.BadGatewayOn = 5;
        using var executor = Executor(provider, new InProcessGate(_gate), clock);

        await executor.StartAsync(CancellationToken.None);
        for (var pause = 2; pause <= 11; pause++)
        {
            Should.CompleteIn(clock.Timer(pause), TimeSpan.FromSeconds(10), $"the executor did not pause after send {pause - 1}");
            clock.Advance(GitHubActionExecutor.BetweenSends);
        }

        await executor.StopAsync(CancellationToken.None);

        _github.SentAt.ShouldBe(Enumerable.Range(0, 10).Select(i => started + (i * GitHubActionExecutor.BetweenSends)));
        var ended = await OutcomesAsync(ids);
        ended[4].ShouldBe((PendingActionStatus.Executed, "failed: github 502"));
        (await _actions.FindAsync(ids[4], CancellationToken.None)).ShouldNotBeNull().Result.ShouldBe("GitHub is currently unavailable. Try again later.");
        ended.Where((_, i) => i != 4).ShouldAllBe(e => e.Status == PendingActionStatus.Executed && e.Outcome == "succeeded");
    }

    [Fact]
    public async Task A_Revoke_Mid_Set_Rejects_The_Unsent_And_Keeps_The_Sent()
    {
        await using var provider = await ComposeAsync();
        var ids = await ApproveTenAsync();
        var executor = Executor(provider, new InProcessGate(_gate), TimeProvider.System);
        for (var i = 0; i < 3; i++)
        {
            await executor.RunOnceAsync(CancellationToken.None);
        }

        await new ActionDecisionService(_states, _actions, _writes).RejectUnstartedAsync(FeatureCatalog.GitHubId, CancellationToken.None);
        await Features().SetWriteGrantAsync(FeatureCatalog.GitHubId, granted: false, confirmWrites: false, CancellationToken.None);
        var afterTheRevoke = await executor.RunOnceAsync(CancellationToken.None);

        afterTheRevoke.ShouldBe(GitHubActionExecutor.Ran.Nothing);
        _github.Titles.ShouldBe(["Finding 0", "Finding 1", "Finding 2"]);
        var ended = await OutcomesAsync(ids);
        ended.Take(3).ShouldAllBe(e => e.Status == PendingActionStatus.Executed && e.Outcome == "succeeded");
        ended.Skip(3).ShouldAllBe(e => e.Status == PendingActionStatus.Rejected && e.Outcome == null);
        var rejections = (await _writes.ListAsync(100, CancellationToken.None)).Where(e => e.Outcome == $"rejected: {WriteGateService.WritesRevoked}").ToList();
        rejections.Select(e => e.ActionId).ShouldBe(ids.Skip(3), ignoreOrder: true);
        rejections.ShouldAllBe(e => e.GroupId == Group);
    }

    [Fact]
    public async Task A_Card_s_Issue_At_The_Plugin_s_Bounds_Opens_Exactly_Once_Across_Two_Clicks()
    {
        await using var provider = await ComposeAsync();
        using var cards = new CardIssuesService(_states, _actions, _writes, NullLogger<CardIssuesService>.Instance);
        var title = new string('t', 256);
        var executor = Executor(provider, new InProcessGate(_gate), TimeProvider.System);

        var first = await cards.ApproveAsync("card-1", "acme/shop", [(title, new string('b', 10_000))], CancellationToken.None);
        var firstRuns = await RunUntilNothingAsync(executor);
        var second = await cards.ApproveAsync("card-1", "acme/shop", [(title, new string('b', 10_000))], CancellationToken.None);
        var secondRuns = await RunUntilNothingAsync(executor);

        firstRuns.ShouldBe([GitHubActionExecutor.Ran.Sent, GitHubActionExecutor.Ran.Nothing]);
        secondRuns.ShouldBe([GitHubActionExecutor.Ran.Nothing], "the second click queues nothing to send");
        _github.Titles.Count.ShouldBe(1, "the issue is opened once");
        _github.Titles.ShouldBe([title]);
        second.Actions.Select(a => a.Id).ShouldBe(first.Actions.Select(a => a.Id));
        (second.Decided, second.Actions.ShouldHaveSingleItem().Status, second.Actions[0].Outcome).ShouldBe((true, PendingActionStatus.Executed, "succeeded"));
    }

    [Fact]
    public async Task Ten_Card_Issues_With_One_Answered_502_A_Second_Click_Sends_Only_The_Failed_One_And_A_Third_Sends_Nothing()
    {
        await using var provider = await ComposeAsync();
        using var cards = new CardIssuesService(_states, _actions, _writes, NullLogger<CardIssuesService>.Instance);
        var executor = Executor(provider, new InProcessGate(_gate), TimeProvider.System);
        _github.BadGatewayOn = 5;
        List<(string Title, string Body)> issues = [.. Enumerable.Range(0, 10).Select(i => ($"Finding {i}", $"Body of finding {i}."))];

        var first = await cards.ApproveAsync("card-1", "acme/shop", issues, CancellationToken.None);
        await RunUntilNothingAsync(executor);
        var sentByTheFirst = _github.Titles.ToList();
        var second = await cards.ApproveAsync("card-1", "acme/shop", issues, CancellationToken.None);
        var secondRuns = await RunUntilNothingAsync(executor);
        var sentByTheSecond = _github.Titles.Skip(sentByTheFirst.Count).ToList();
        var third = await cards.ApproveAsync("card-1", "acme/shop", issues, CancellationToken.None);
        var thirdRuns = await RunUntilNothingAsync(executor);

        sentByTheFirst.ShouldBe(issues.Select(i => i.Title), "the first click sends each issue once, the fifth answered 502");
        (await _actions.FindAsync(first.Actions[4].Id, CancellationToken.None)).ShouldNotBeNull().Outcome.ShouldBe("failed: github 502");
        second.Actions.Select((a, i) => a.Id == first.Actions[i].Id).ShouldBe(Enumerable.Range(0, 10).Select(i => i != 4), "only the failed issue is queued again");
        secondRuns.ShouldBe([GitHubActionExecutor.Ran.Sent, GitHubActionExecutor.Ran.Nothing]);
        sentByTheSecond.ShouldBe(["Finding 4"], "a second click sends only the issue that failed");
        thirdRuns.ShouldBe([GitHubActionExecutor.Ran.Nothing], "a third click sends nothing");
        _github.Titles.Count.ShouldBe(11, "no issue that was sent is ever sent again");
        third.Actions.Select(a => a.Id).ShouldBe(second.Actions.Select(a => a.Id));
        (third.Decided, third.Mismatches.Count).ShouldBe((true, 0));
        third.Actions.ShouldAllBe(a => a.Status == PendingActionStatus.Executed && a.Outcome == "succeeded");
    }

    [Fact]
    public async Task A_Card_Issue_Sent_By_A_Worker_That_Stopped_Before_Recording_It_Across_A_Re_Grant_Is_Never_Sent_Again_By_A_Later_Click()
    {
        await using var provider = await ComposeAsync();
        using var cards = new CardIssuesService(_states, _actions, _writes, NullLogger<CardIssuesService>.Instance);
        var gate = new InProcessGate(_gate);
        var executor = Executor(provider, gate, TimeProvider.System);
        List<(string Title, string Body)> issues = [("Finding 0", "Body of finding 0.")];

        var first = await cards.ApproveAsync("card-1", "acme/shop", issues, CancellationToken.None);
        gate.RecordFailure = new IOException("The worker stopped.");
        await executor.RunOnceAsync(CancellationToken.None);
        gate.RecordFailure = null;
        await Features().SetWriteGrantAsync(FeatureCatalog.GitHubId, granted: false, confirmWrites: false, CancellationToken.None);
        await Features().SetWriteGrantAsync(FeatureCatalog.GitHubId, granted: true, confirmWrites: true, CancellationToken.None);
        await LapseClaimAsync(first.Actions[0].Id);
        var afterTheLapse = await RunUntilNothingAsync(executor);
        var second = await cards.ApproveAsync("card-1", "acme/shop", issues, CancellationToken.None);
        var afterTheSecondClick = await RunUntilNothingAsync(executor);

        afterTheLapse.ShouldBe([GitHubActionExecutor.Ran.Nothing]);
        var answered = second.Actions.ShouldHaveSingleItem();
        (answered.Id, answered.Status, answered.Reason).ShouldBe(
            (first.Actions[0].Id, PendingActionStatus.Rejected, (string?)WriteGateService.NotConfirmed("GitHub")), "the click answers the row that may already be on GitHub");
        afterTheSecondClick.ShouldBe([GitHubActionExecutor.Ran.Nothing], "a later click queues nothing to send");
        _github.Titles.ToList().ShouldBe(["Finding 0"], "the fake GitHub receives no second POST for that issue");
    }

    private static async Task<List<GitHubActionExecutor.Ran>> RunUntilNothingAsync(GitHubActionExecutor executor)
    {
        var runs = new List<GitHubActionExecutor.Ran>();
        do
        {
            runs.Add(await executor.RunOnceAsync(CancellationToken.None));
        }
        while (runs[^1] != GitHubActionExecutor.Ran.Nothing && runs.Count < 20);

        return runs;
    }

    private async Task<List<string>> ApproveTenAsync()
    {
        var ids = new List<string>();
        for (var i = 0; i < 10; i++)
        {
            var queued = await _gate.RequestAsync(
                FeatureCatalog.GitHubId,
                "New issues",
                "GitHubActions.create_issue",
                "create_issue(owner=acme, repo=shop, title, body)",
                $$"""{"owner":"acme","repo":"shop","title":"Finding {{i}}","body":"Body of finding {{i}}."}""",
                Group,
                "acme/shop",
                CancellationToken.None);
            ids.Add(queued.ActionId.ShouldNotBeNull());
        }

        await _gate.CloseGroupAsync(Group, CancellationToken.None);
        var listed = (await new ActionDecisionService(_states, _actions, _writes).ListPendingAsync(CancellationToken.None))
            .Select(l => (l.Action.Id, l.Action.PayloadDigest()))
            .ToList();
        (await new ActionDecisionService(_states, _actions, _writes).ApproveSetAsync(Group, listed, null, CancellationToken.None)).Decided.ShouldBeTrue();
        return ids;
    }

    private async Task<List<(PendingActionStatus Status, string? Outcome)>> OutcomesAsync(IEnumerable<string> ids)
    {
        var outcomes = new List<(PendingActionStatus, string?)>();
        foreach (var id in ids)
        {
            var action = (await _actions.FindAsync(id, CancellationToken.None)).ShouldNotBeNull();
            outcomes.Add((action.Status, action.Outcome));
        }

        return outcomes;
    }

    private async Task LapseClaimAsync(string actionId)
    {
        var all = JsonNode.Parse(await File.ReadAllTextAsync(_actions.FilePath))!.AsArray();
        all.Single(a => a!["id"]!.GetValue<string>() == actionId)!["claimedAt"] =
            (DateTimeOffset.UtcNow - WriteGateService.ClaimLease - TimeSpan.FromMinutes(1)).ToString("O", CultureInfo.InvariantCulture);
        await File.WriteAllTextAsync(_actions.FilePath, all.ToJsonString());
    }

    private static GitHubActionExecutor Executor(ServiceProvider provider, InProcessGate gate, TimeProvider clock) => new(
        gate, gate, provider.GetRequiredService<GitHubVerdictRunner>(), provider.GetRequiredService<GitHubWriteRunner>(), clock, NullLogger<GitHubActionExecutor>.Instance);

    private FeaturesService Features() =>
        new(new FeatureCatalog(), _states, new InMemoryCredentialStore(), new NullFeatureActivator(), Array.Empty<IFeatureCredentialValidator>());

    private async Task<ServiceProvider> ComposeAsync()
    {
        await Features().SetWriteGrantAsync(FeatureCatalog.GitHubId, granted: true, confirmWrites: true, CancellationToken.None);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Scout:Llm:Provider"] = "Ollama",
                ["Scout:Plugins:GitHub:Token"] = "github-dummy-not-real",
            })
            .Build();
        var services = new ServiceCollection().AddLogging().AddScoutLlm(configuration).AddGitHubPlugin(configuration);
        services.AddHttpClient("IGitHubClient").ConfigurePrimaryHttpMessageHandler(() => _github);
        services.AddHttpClient("IGitHubWriteClient").ConfigurePrimaryHttpMessageHandler(() => _github);
        services.AddSingleton<IWriteGate>(new InProcessGate(_gate));
        return services.BuildServiceProvider();
    }

    private sealed class InProcessGate(WriteGateService gate) : IWriteGate, IApprovedActions
    {
        public Exception? RecordFailure { get; set; }

        public async Task<ApprovedAction?> ClaimNextAsync(string featureId, CancellationToken cancellationToken) =>
            await gate.ClaimNextApprovedAsync(featureId, cancellationToken) is { } claimed
                ? new ApprovedAction(claimed.Id, claimed.FeatureId, claimed.Action, claimed.Target, claimed.Summary) { Payload = claimed.Payload }
                : null;

        public Task<bool> CheckGrantAsync(ApprovedAction action, CancellationToken cancellationToken) =>
            gate.CheckGrantAsync(action.FeatureId, action.Id, cancellationToken);

        public async Task<WriteGateDecision> RequestAsync(string featureId, string scope, string target, string summary, CancellationToken cancellationToken)
        {
            var decision = await gate.RequestAsync(featureId, scope, target, summary, cancellationToken);
            return new WriteGateDecision(
                decision.Kind == WriteDecisionKind.Queued ? WriteGateDecisionKind.Queued : WriteGateDecisionKind.Refused, decision.Reason, decision.ActionId);
        }

        public Task RecordAsync(string featureId, string scope, string target, string outcome, string? actionId, CancellationToken cancellationToken) =>
            RecordAsync(featureId, scope, target, outcome, actionId, null, null, cancellationToken);

        public Task RecordAsync(
            string featureId, string scope, string target, string outcome, string? actionId, string? result, string? signedAs, CancellationToken cancellationToken) =>
            RecordFailure is null
                ? gate.RecordAsync(featureId, scope, target, outcome, actionId, result, signedAs, cancellationToken)
                : Task.FromException(RecordFailure);
    }

    private sealed class FakeGitHub : HttpMessageHandler
    {
        private int _posts;

        public TimeProvider Clock { get; set; } = TimeProvider.System;

        public int BadGatewayOn { get; set; }

        public ConcurrentQueue<string> Titles { get; } = new();

        public ConcurrentQueue<DateTimeOffset> SentAt { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method != HttpMethod.Post)
            {
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
            }

            var sent = Interlocked.Increment(ref _posts);
            SentAt.Enqueue(Clock.GetUtcNow());
            Titles.Enqueue(JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!["title"]!.GetValue<string>());
            return sent == BadGatewayOn
                ? new HttpResponseMessage(HttpStatusCode.BadGateway) { Content = new StringContent("""{"message":"Server Error"}""") }
                : new HttpResponseMessage(HttpStatusCode.Created)
                {
                    Content = new StringContent($$$"""{"number":{{{sent}}},"html_url":"https://github.com/acme/shop/issues/{{{sent}}}","user":{"login":"scout"}}"""),
                };
        }
    }

    private sealed class SignallingClock : FakeTimeProvider
    {
        private readonly List<TaskCompletionSource> _timers = [];
        private int _created;

        public Task Timer(int number) => Nth(number).Task;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = base.CreateTimer(callback, state, dueTime, period);
            Nth(Interlocked.Increment(ref _created)).TrySetResult();
            return timer;
        }

        private TaskCompletionSource Nth(int number)
        {
            lock (_timers)
            {
                while (_timers.Count < number)
                {
                    _timers.Add(new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
                }

                return _timers[number - 1];
            }
        }
    }
}
