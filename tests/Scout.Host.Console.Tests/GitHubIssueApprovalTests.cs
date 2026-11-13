using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;
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
/// The model's <c>submit_pull_request_review</c>, <c>create_issue</c> and comment calls through the kernel's write gate, over the real catalog, Scout's own state, queue and
/// log in a temp folder, with GitHub writes granted: each review, issue and comment waits for the owner's approval and none reaches GitHub,
/// and the worker's executor over the same queue sends one only after the owner approved it, once.
/// </summary>
public sealed class GitHubIssueApprovalTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("scout-issue-approval-").FullName;
    private readonly JsonFileFeatureStateStore _states;
    private readonly JsonFileActionQueue _actions;
    private readonly JsonLinesWriteLog _writes;
    private readonly CountingGitHub _github = new();

    public GitHubIssueApprovalTests()
    {
        var options = Options.Create(new FeatureStateStoreOptions { FilePath = Path.Combine(_directory, "features.json") });
        _states = new JsonFileFeatureStateStore(options, NullLogger<JsonFileFeatureStateStore>.Instance);
        _actions = new JsonFileActionQueue(options, NullLogger<JsonFileActionQueue>.Instance);
        _writes = new JsonLinesWriteLog(options, NullLogger<JsonLinesWriteLog>.Instance);
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
    public async Task Still_Opening_Issues_For_Everything_A_Turn_Asking_Three_Issues_Leaves_Three_Approval_Cards_And_Opens_None()
    {
        await using var provider = await ComposeAsync();
        var kernel = provider.GetRequiredService<Kernel>();
        var createIssue = provider.GetServices<KernelPlugin>().Single(p => p.Name == "GitHubActions")["create_issue"];

        var answers = new List<string?>();
        foreach (var title in new[] { "Discounts applied after the sum", "Cart loses an item", "No lock on move" })
        {
            var result = await kernel.InvokeAsync(
                createIssue, new KernelArguments { ["owner"] = "acme", ["repo"] = "shop", ["title"] = title, ["body"] = "## Problem\n\nWrong." });
            answers.Add(result.GetValue<string>());
        }

        var waiting = await _actions.ListPendingAsync(CancellationToken.None);
        answers.ShouldAllBe(a => a != null && a.StartsWith("Queued for the owner's approval", StringComparison.Ordinal));
        waiting.Select(a => a.Action).ShouldBe(["New issues", "New issues", "New issues"]);
        waiting.Select(a => a.Payload).ShouldAllBe(p => p != null && p.Contains("\"repo\":\"shop\"", StringComparison.Ordinal));
        waiting[1].Payload.ShouldNotBeNull().ShouldContain("Cart loses an item");
        _github.Requests.ShouldBe(0, "no issue may reach GitHub before the owner approves it");
        (await _writes.ListAsync(10, CancellationToken.None)).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_Model_Call_Asking_To_Post_A_Review_Posts_Nothing_And_Leaves_One_Card_Waiting_For_The_Owner()
    {
        await using var provider = await ComposeAsync();
        var kernel = provider.GetRequiredService<Kernel>();
        var submit = provider.GetServices<KernelPlugin>().Single(p => p.Name == "GitHubActions")["submit_pull_request_review"];

        var answer = (await kernel.InvokeAsync(submit, new KernelArguments
        {
            ["owner"] = "acme",
            ["repo"] = "shop",
            ["number"] = 7,
            ["body"] = "Two findings.",
            ["comments"] = """[{"path":"src/Cart.cs","line":12,"body":"Two moves lose an item."}]""",
        })).GetValue<string>();

        var waiting = (await _actions.ListPendingAsync(CancellationToken.None)).ShouldHaveSingleItem();
        answer.ShouldNotBeNull().ShouldStartWith("Queued for the owner's approval");
        (waiting.Action, waiting.Target, waiting.Status).ShouldBe(("Pull request reviews", "GitHubActions.submit_pull_request_review", PendingActionStatus.Pending));
        waiting.Payload.ShouldNotBeNull().ShouldContain("Two moves lose an item.");
        _github.Requests.ShouldBe(0, "no review may reach GitHub before the owner approves it");
        (await _writes.ListAsync(10, CancellationToken.None)).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("comment_on_pull_request")]
    [InlineData("comment_on_issue")]
    [InlineData("reply_to_review_comment")]
    public async Task Flooded_The_Pull_Request_With_Comments_Each_Comment_Asked_Waits_For_Its_Card_And_None_Is_Posted(string function)
    {
        await using var provider = await ComposeAsync();
        var kernel = provider.GetRequiredService<Kernel>();
        var comment = provider.GetServices<KernelPlugin>().Single(p => p.Name == "GitHubActions")[function];

        var answers = new List<string?>();
        foreach (var body in new[] { "Nit: rename this.", "Nit: extract a method.", "Nit: add a test." })
        {
            var result = await kernel.InvokeAsync(
                comment, new KernelArguments { ["owner"] = "acme", ["repo"] = "shop", ["number"] = 7, ["comment_id"] = 99L, ["body"] = body });
            answers.Add(result.GetValue<string>());
        }

        var waiting = await _actions.ListPendingAsync(CancellationToken.None);
        answers.ShouldAllBe(a => a != null && a.StartsWith("Queued for the owner's approval", StringComparison.Ordinal));
        waiting.Count.ShouldBe(3);
        waiting.Select(a => a.Target).ShouldAllBe(t => t == "GitHubActions." + function);
        waiting[2].Payload.ShouldNotBeNull().ShouldContain("Nit: add a test.");
        _github.Requests.ShouldBe(0, "no comment may reach GitHub before the owner approves it");
        (await _writes.ListAsync(10, CancellationToken.None)).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("submit_pull_request_review", "repos/acme/shop/pulls/7/reviews")]
    [InlineData("create_issue", "repos/acme/shop/issues")]
    [InlineData("comment_on_pull_request", "repos/acme/shop/issues/7/comments")]
    [InlineData("comment_on_issue", "repos/acme/shop/issues/7/comments")]
    [InlineData("reply_to_review_comment", "repos/acme/shop/pulls/7/comments/99/replies")]
    [InlineData("give_pull_request_verdict", "repos/acme/shop/pulls/7/reviews")]
    public async Task A_Write_The_Model_Asked_For_Reaches_GitHub_Only_After_The_Owner_Approves_That_Action_Never_On_The_Grant_Alone_And_Then_Exactly_Once(
        string function, string route)
    {
        await using var provider = await ComposeAsync();
        var kernel = provider.GetRequiredService<Kernel>();
        var gate = (InProcessGate)provider.GetRequiredService<IWriteGate>();
        var executor = new GitHubActionExecutor(
            gate, gate, provider.GetRequiredService<GitHubVerdictRunner>(), provider.GetRequiredService<GitHubWriteRunner>(), TimeProvider.System, NullLogger<GitHubActionExecutor>.Instance);
        var decisions = new ActionDecisionService(_states, _actions, _writes);

        await kernel.InvokeAsync(provider.GetServices<KernelPlugin>().Single(p => p.Name == "GitHubActions")[function], Asked());
        await executor.RunOnceAsync(CancellationToken.None);
        _github.Posts.ShouldBeEmpty("the grant alone must send nothing: the owner has not approved this action");
        var waiting = (await _actions.ListPendingAsync(CancellationToken.None)).ShouldHaveSingleItem();
        await decisions.ApproveAsync(waiting.Id, null, CancellationToken.None);
        await executor.RunOnceAsync(CancellationToken.None);
        await executor.RunOnceAsync(CancellationToken.None);

        _github.Posts.ShouldBe([route]);
        (await _actions.FindAsync(waiting.Id, CancellationToken.None)).ShouldNotBeNull().Status.ShouldBe(PendingActionStatus.Executed);
        (await _writes.ListAsync(10, CancellationToken.None)).Select(e => (e.Outcome, e.ActionId)).ShouldBe([("succeeded", waiting.Id), ("approved", waiting.Id)]);
    }

    [Theory]
    [InlineData("submit_pull_request_review", "Pull request reviews", "repos/acme/shop/pulls/7/reviews")]
    [InlineData("give_pull_request_verdict", "Pull request verdicts", "repos/acme/shop/pulls/7/reviews")]
    [InlineData("comment_on_pull_request", "Pull request comments", "repos/acme/shop/issues/7/comments")]
    [InlineData("reply_to_review_comment", "Pull request comments", "repos/acme/shop/pulls/7/comments/99/replies")]
    [InlineData("create_issue", "New issues", "repos/acme/shop/issues")]
    [InlineData("comment_on_issue", "Issue comments", "repos/acme/shop/issues/7/comments")]
    public async Task An_Approved_Action_A_Worker_Sent_And_Never_Recorded_Is_Not_Sent_Again_Once_Its_Claim_Lapses_And_Is_Recorded_As_Failed_Saying_It_May_Already_Be_On_GitHub(
        string function, string row, string route)
    {
        await using var provider = await ComposeAsync();
        var kernel = provider.GetRequiredService<Kernel>();
        var gate = (InProcessGate)provider.GetRequiredService<IWriteGate>();
        GitHubActionExecutor Worker() => new(
            gate, gate, provider.GetRequiredService<GitHubVerdictRunner>(), provider.GetRequiredService<GitHubWriteRunner>(), TimeProvider.System, NullLogger<GitHubActionExecutor>.Instance);
        await kernel.InvokeAsync(provider.GetServices<KernelPlugin>().Single(p => p.Name == "GitHubActions")[function], Asked());
        var waiting = (await _actions.ListPendingAsync(CancellationToken.None)).ShouldHaveSingleItem();
        await new ActionDecisionService(_states, _actions, _writes).ApproveAsync(waiting.Id, null, CancellationToken.None);
        gate.RecordFailure = new IOException("The control plane is gone.");
        await Worker().RunOnceAsync(CancellationToken.None);
        gate.RecordFailure = null;
        var sentByTheFirstWorker = _github.Posts.ToArray();
        await LapseClaimAsync(waiting.Id);

        var restarted = Worker();
        await restarted.RunOnceAsync(CancellationToken.None);
        await restarted.RunOnceAsync(CancellationToken.None);

        sentByTheFirstWorker.ShouldBe([route]);
        _github.Posts.Count.ShouldBe(1, "the first worker sent it and stopped before the record, so a second worker must not send it again");
        var ended = (await _actions.FindAsync(waiting.Id, CancellationToken.None)).ShouldNotBeNull();
        (ended.Action, ended.Status, ended.Outcome, ended.Result).ShouldBe((
            row,
            PendingActionStatus.Executed,
            "failed: not confirmed",
            "Scout stopped before it recorded this, so it may already be on GitHub: look there before asking for it again."));
        (await _writes.ListAsync(10, CancellationToken.None)).Select(e => (e.Outcome, e.ActionId)).ShouldBe([("failed: not confirmed", waiting.Id), ("approved", waiting.Id)]);
    }

    private static KernelArguments Asked() => new()
    {
        ["owner"] = "acme",
        ["repo"] = "shop",
        ["number"] = 7,
        ["comment_id"] = 99L,
        ["title"] = "Cart loses an item",
        ["body"] = "Two moves lose an item.",
        ["comments"] = """[{"path":"src/Cart.cs","line":3,"body":"Two moves lose an item."}]""",
        ["verdict"] = "request_changes",
        ["comments_json"] = """[{"path":"src/Cart.cs","line":3,"body":"Two moves lose an item."}]""",
        ["commit"] = CountingGitHub.Head,
    };

    private async Task LapseClaimAsync(string actionId)
    {
        var all = JsonNode.Parse(await File.ReadAllTextAsync(_actions.FilePath))!.AsArray();
        all.Single(a => a!["id"]!.GetValue<string>() == actionId)!["claimedAt"] =
            (DateTimeOffset.UtcNow - WriteGateService.ClaimLease - TimeSpan.FromMinutes(1)).ToString("O", CultureInfo.InvariantCulture);
        await File.WriteAllTextAsync(_actions.FilePath, all.ToJsonString());
    }

    [Fact]
    public async Task A_Review_The_Model_Asks_For_With_A_Card_s_Commit_Added_Stays_Its_Own_Waiting_Action_And_A_Later_Post_On_That_Card_Neither_Adopts_Nor_Approves_It()
    {
        await using var provider = await ComposeAsync();
        var kernel = provider.GetRequiredService<Kernel>();
        var asked = Asked();
        asked["commit"] = CountingGitHub.Head;

        await kernel.InvokeAsync(provider.GetServices<KernelPlugin>().Single(p => p.Name == "GitHubActions")["submit_pull_request_review"], asked);
        var fromTheModel = (await _actions.ListPendingAsync(CancellationToken.None)).ShouldHaveSingleItem();
        var fromTheCard = await new PullRequestVerdictService(_states, _actions, _writes).GiveAsync(
            new PullRequestVerdictRequest(
                "acme", "shop", 7, "comment", "Two moves lose an item.", [new VerdictComment("src/Cart.cs", 3, "Two moves lose an item.")], CountingGitHub.Head, null),
            CancellationToken.None);

        fromTheModel.Payload.ShouldNotBeNull().ShouldNotContain("commit");
        fromTheCard.Id.ShouldNotBe(fromTheModel.Id);
        (fromTheCard.Action, fromTheCard.Status).ShouldBe(("Pull request reviews", PendingActionStatus.Approved));
        var afterThePost = (await _actions.FindAsync(fromTheModel.Id, CancellationToken.None)).ShouldNotBeNull();
        (afterThePost.Status, afterThePost.Reason).ShouldBe((PendingActionStatus.Pending, (string?)null));
        _github.Posts.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_Verdict_Asked_In_Chat_Carrying_A_Tests_Line_The_Owner_Never_Saw_Is_Given_Once_Approved_And_Posts_No_Comment()
    {
        await using var provider = await ComposeAsync();
        var kernel = provider.GetRequiredService<Kernel>();
        var gate = (InProcessGate)provider.GetRequiredService<IWriteGate>();
        var executor = new GitHubActionExecutor(
            gate, gate, provider.GetRequiredService<GitHubVerdictRunner>(), provider.GetRequiredService<GitHubWriteRunner>(), TimeProvider.System, NullLogger<GitHubActionExecutor>.Instance);

        await kernel.InvokeAsync(provider.GetServices<KernelPlugin>().Single(p => p.Name == "GitHubActions")["give_pull_request_verdict"], new KernelArguments
        {
            ["owner"] = "acme",
            ["repo"] = "shop",
            ["number"] = 7,
            ["verdict"] = "request_changes",
            ["body"] = "Two moves lose an item.",
            ["comments_json"] = """[{"path":"src/Cart.cs","line":3,"body":"Two moves lose an item."}]""",
            ["commit"] = CountingGitHub.Head,
            ["testsLine"] = "A comment the owner never saw.",
        });
        var waiting = (await _actions.ListPendingAsync(CancellationToken.None)).ShouldHaveSingleItem();
        await new ActionDecisionService(_states, _actions, _writes).ApproveAsync(waiting.Id, null, CancellationToken.None);
        await executor.RunOnceAsync(CancellationToken.None);

        _github.Posts.ShouldBe(["repos/acme/shop/pulls/7/reviews"]);
        waiting.Payload.ShouldNotBeNull().ShouldNotContain("testsLine");
    }

    private async Task<ServiceProvider> ComposeAsync()
    {
        await new FeaturesService(new FeatureCatalog(), _states, new InMemoryCredentialStore(), new NullFeatureActivator(), Array.Empty<IFeatureCredentialValidator>())
            .SetWriteGrantAsync(FeatureCatalog.GitHubId, granted: true, confirmWrites: true, CancellationToken.None);
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
        services.AddSingleton<IWriteGate>(new InProcessGate(new WriteGateService(new FeatureCatalog(), _states, _actions, _writes)));
        return services.BuildServiceProvider();
    }

    private sealed class InProcessGate(WriteGateService gate) : IWriteGate, IApprovedActions
    {
        public async Task<ApprovedAction?> ClaimNextAsync(string featureId, CancellationToken cancellationToken) =>
            await gate.ClaimNextApprovedAsync(featureId, cancellationToken) is { } claimed
                ? new ApprovedAction(claimed.Id, claimed.FeatureId, claimed.Action, claimed.Target, claimed.Summary) { Payload = claimed.Payload }
                : null;

        public Task<bool> CheckGrantAsync(ApprovedAction action, CancellationToken cancellationToken) =>
            gate.CheckGrantAsync(action.FeatureId, action.Id, cancellationToken);

        public Exception? RecordFailure { get; set; }

        public Task RecordAsync(
            string featureId, string scope, string target, string outcome, string? actionId, string? result, string? signedAs, CancellationToken cancellationToken) =>
            RecordFailure is null
                ? gate.RecordAsync(featureId, scope, target, outcome, actionId, result, signedAs, cancellationToken)
                : Task.FromException(RecordFailure);

        public Task<WriteGateDecision> RequestAsync(string featureId, string scope, string target, string summary, CancellationToken cancellationToken) =>
            RequestAsync(featureId, scope, target, summary, null, cancellationToken);

        public async Task<WriteGateDecision> RequestAsync(
            string featureId, string scope, string target, string summary, string? payload, CancellationToken cancellationToken)
        {
            var decision = await gate.RequestAsync(featureId, scope, target, summary, payload, cancellationToken);
            var kind = decision.Kind switch
            {
                WriteDecisionKind.Allowed => WriteGateDecisionKind.Allowed,
                WriteDecisionKind.Queued => WriteGateDecisionKind.Queued,
                _ => WriteGateDecisionKind.Refused,
            };
            return new WriteGateDecision(kind, decision.Reason, decision.ActionId);
        }

        public Task RecordAsync(string featureId, string scope, string target, string outcome, string? actionId, CancellationToken cancellationToken) =>
            gate.RecordAsync(featureId, scope, target, outcome, actionId, cancellationToken);
    }

    private sealed class CountingGitHub : HttpMessageHandler
    {
        public const string Head = "0123456789abcdef0123456789abcdef01234567";

        private const string Files =
            """[{"filename":"src/Cart.cs","status":"modified","additions":2,"deletions":1,"patch":"@@ -1,3 +1,4 @@\n a\n-b\n+c\n+d\n e"}]""";

        private int _requests;

        public int Requests => _requests;

        public ConcurrentQueue<string> Posts { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requests);
            var path = request.RequestUri!.PathAndQuery.TrimStart('/');
            if (request.Method == HttpMethod.Post)
            {
                Posts.Enqueue(path);
            }

            var body = path.Contains("/files", StringComparison.Ordinal)
                ? Files
                : request.Method == HttpMethod.Get && path.Contains("/reviews", StringComparison.Ordinal)
                    ? "[]"
                    : $$$"""{"number":1,"login":"octocat","html_url":"https://github.com/acme/shop/issues/1","user":{"login":"alice"},"head":{"ref":"feat/x","sha":"{{{Head}}}"}}""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Created) { Content = new StringContent(body) });
        }
    }
}
