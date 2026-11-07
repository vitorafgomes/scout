using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Scout.ControlPlane.Actions;
using Scout.ControlPlane.Features;
using Scout.ControlPlane.WriteGate;
using Shouldly;
using Xunit;

namespace Scout.ControlPlane.Tests.Features;

/// <summary>The GitHub rows of the catalog as the write gate reads them, over Scout's own state, queue and log in a temp folder.</summary>
public sealed class GitHubWriteRowsGateTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("scout-github-rows-").FullName;
    private readonly JsonFileFeatureStateStore _states;
    private readonly JsonFileActionQueue _actions;
    private readonly JsonLinesWriteLog _writes;

    public GitHubWriteRowsGateTests()
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
        Directory.Delete(_directory, recursive: true);
    }

    private WriteGateService Gate() => new(new FeatureCatalog(), _states, _actions, _writes);

    private Task<FeatureSnapshot> GrantGitHubAsync() =>
        new FeaturesService(new FeatureCatalog(), _states, new InMemoryCredentialStore(), new NullFeatureActivator(), Array.Empty<IFeatureCredentialValidator>())
            .SetWriteGrantAsync(FeatureCatalog.GitHubId, granted: true, confirmWrites: true, CancellationToken.None);

    [Fact]
    public async Task A_Verdict_On_A_Pull_Request_Waits_For_The_Owner_s_Approval_Of_That_Action_Even_Under_The_Grant()
    {
        await GrantGitHubAsync();

        var decision = await Gate().RequestAsync(
            "github", "Pull request verdicts", "GitHubActions.give_pull_request_verdict",
            "give_pull_request_verdict(body, comments_json, number=42, owner=octocat, repo=hello, verdict=approve)", CancellationToken.None);

        decision.Kind.ShouldBe(WriteDecisionKind.Queued);
        (await _actions.ListPendingAsync(CancellationToken.None)).ShouldHaveSingleItem().Action.ShouldBe("Pull request verdicts");
    }

    [Fact]
    public async Task A_Verdict_Is_Never_Recorded_Without_An_Action_The_Owner_Approved()
    {
        await GrantGitHubAsync();

        var ex = await Should.ThrowAsync<WriteNotRecordableException>(() => Gate().RecordAsync(
            "github", "Pull request verdicts", "GitHubActions.give_pull_request_verdict", "succeeded", null, CancellationToken.None));

        ex.Message.ShouldBe("Pull request verdicts is irreversible; it is recorded only with an action the owner approved.");
    }

    [Theory]
    [InlineData("Pull request comments")]
    [InlineData("Issue comments")]
    public async Task A_Comment_Runs_Under_The_Grant_Without_An_Approval_And_Is_Refused_Without_It(string row)
    {
        var refused = await Gate().RequestAsync("github", row, "GitHubActions.comment", "comment(number=42)", CancellationToken.None);
        await GrantGitHubAsync();
        var allowed = await Gate().RequestAsync("github", row, "GitHubActions.comment", "comment(number=42)", CancellationToken.None);

        refused.ShouldBe(new WriteDecision(WriteDecisionKind.Refused, "The owner has not allowed github to write; allow it on the Features screen of Scout Desktop."));
        allowed.ShouldBe(new WriteDecision(WriteDecisionKind.Allowed, string.Empty));
        (await _actions.ListPendingAsync(CancellationToken.None)).ShouldBeEmpty();
    }
}
