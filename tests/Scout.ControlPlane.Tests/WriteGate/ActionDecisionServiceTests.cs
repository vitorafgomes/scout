using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Scout.ControlPlane.Actions;
using Scout.ControlPlane.Features;
using Scout.ControlPlane.WriteGate;
using Shouldly;
using Xunit;

namespace Scout.ControlPlane.Tests.WriteGate;

public sealed class ActionDecisionServiceTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("scout-decisions-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public async Task A_Write_Log_That_Cannot_Be_Written_Fails_The_Approval_With_The_Owner_s_Decision_Kept()
    {
        var options = Options.Create(new FeatureStateStoreOptions { FilePath = Path.Combine(_directory, "features.json") });
        var states = new JsonFileFeatureStateStore(options, NullLogger<JsonFileFeatureStateStore>.Instance);
        using var queue = new JsonFileActionQueue(options, NullLogger<JsonFileActionQueue>.Instance);
        var log = Substitute.For<IWriteLog>();
        log.AppendAsync(Arg.Any<WriteLogEntry>(), Arg.Any<CancellationToken>()).ThrowsAsync(new IOException("disk full"));
        await states.SetAsync(FeatureCatalog.GitHubId, new FeatureState(false, null, new Dictionary<string, string?>(), true, DateTimeOffset.UtcNow.AddMinutes(-1)), CancellationToken.None);
        var waiting = await queue.EnqueueAsync(FeatureCatalog.GitHubId, "Pull request verdicts", "o/r#1", "Approve o/r#1", CancellationToken.None);
        var decisions = new ActionDecisionService(states, queue, log);

        var failed = await Should.ThrowAsync<IOException>(() => decisions.ApproveAsync(waiting.Id, null, CancellationToken.None));

        failed.Message.ShouldBe("disk full");
        (await queue.FindAsync(waiting.Id, CancellationToken.None)).ShouldNotBeNull().Status.ShouldBe(PendingActionStatus.Approved);
    }

    [Fact]
    public async Task The_Owner_s_Single_Approval_Of_An_Action_Queued_In_A_Request_Is_Logged_With_That_Request_s_Group()
    {
        var options = Options.Create(new FeatureStateStoreOptions { FilePath = Path.Combine(_directory, "features.json") });
        using var states = new JsonFileFeatureStateStore(options, NullLogger<JsonFileFeatureStateStore>.Instance);
        using var queue = new JsonFileActionQueue(options, NullLogger<JsonFileActionQueue>.Instance);
        using var log = new JsonLinesWriteLog(options, NullLogger<JsonLinesWriteLog>.Instance);
        await states.SetAsync(FeatureCatalog.GitHubId, new FeatureState(false, null, new Dictionary<string, string?>(), true, DateTimeOffset.UtcNow.AddMinutes(-1)), CancellationToken.None);
        var waiting = await queue.EnqueueAsync(FeatureCatalog.GitHubId, "New issues", "GitHubActions.create_issue", "s", "{}", "turn-7", "acme/shop", CancellationToken.None);

        await new ActionDecisionService(states, queue, log).ApproveAsync(waiting.Id, null, CancellationToken.None);

        (await log.ListAsync(10, CancellationToken.None)).Select(e => (e.Outcome, e.ActionId, e.GroupId)).ShouldBe([("approved", waiting.Id, "turn-7")]);
    }
}
