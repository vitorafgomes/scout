using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Scout.ControlPlane.Actions;
using Scout.ControlPlane.Features;
using Scout.ControlPlane.Operator;
using Scout.ControlPlane.WriteGate;
using Scout.Operator;
using Shouldly;
using Xunit;

namespace Scout.Host.Console.Tests;

/// <summary>
/// What Scout.Operator sends, checked against the control plane that validates it: the statuses, the claim
/// status and the scope of its declared write.
/// </summary>
public sealed class OperatorContractTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("scout-operator-contract-").FullName;
    private readonly JsonFileFeatureStateStore _states;
    private readonly JsonFileActionQueue _actions;
    private readonly JsonLinesWriteLog _writes;
    private readonly JsonFileOperatorStore _store;

    public OperatorContractTests()
    {
        var options = Options.Create(new FeatureStateStoreOptions { FilePath = Path.Combine(_directory, "features.json") });
        _states = new JsonFileFeatureStateStore(options, NullLogger<JsonFileFeatureStateStore>.Instance);
        _actions = new JsonFileActionQueue(options, NullLogger<JsonFileActionQueue>.Instance);
        _writes = new JsonLinesWriteLog(options, NullLogger<JsonLinesWriteLog>.Instance);
        _store = new JsonFileOperatorStore(options, NullLogger<JsonFileOperatorStore>.Instance);
    }

    public void Dispose()
    {
        _states.Dispose();
        _actions.Dispose();
        _writes.Dispose();
        _store.Dispose();
        Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public async Task Every_Project_Status_And_Claim_Status_The_Operator_Sends_Is_Accepted_By_The_Control_Planes_Worker_Validation()
    {
        var features = new FeaturesService(
            new FeatureCatalog(), _states, new InMemoryCredentialStore(), new NullFeatureActivator(), []);
        await features.SetWriteGrantAsync(FeatureCatalog.YouTubeId, granted: true, confirmWrites: true, CancellationToken.None);
        var service = new OperatorService(_store, new WriteGateService(new FeatureCatalog(), _states, _actions, _writes), _writes);
        string[] sent = [OperatorPipeline.Researching, OperatorPipeline.Scripting, OperatorPipeline.ScriptReady, OperatorPipeline.Failed];

        var saved = new List<OperatorProject>();
        foreach (var status in sent)
        {
            saved.Add(await service.SaveProjectFromWorkerAsync(
                new OperatorProject(string.Empty, "Why rivers meander", status, string.Empty, default, default, [], null,
                    [new Claim("c1", "Most rivers meander.", "standard", [], OperatorPipeline.Pending)]),
                null,
                CancellationToken.None));
        }

        saved.Select(p => p.Status).ShouldBe(sent);
        saved.SelectMany(p => p.Claims).Select(c => c.Status).ShouldAllBe(s => s == OperatorPipeline.Pending);
    }

    [Fact]
    public void The_Operators_Data_Folder_Scope_Is_The_Text_Of_The_Youtube_Write_Row_Of_The_Catalog()
    {
        var row = new FeatureCatalog().Find(FeatureCatalog.YouTubeId).ShouldNotBeNull().ScopeMatrix.ShouldNotBeNull()
            .Where(r => r.Access == ScopeAccess.Write)
            .ShouldHaveSingleItem();

        OperatorServiceCollectionExtensions.DataFolderScope.ShouldBe(row.Scope);
    }
}
