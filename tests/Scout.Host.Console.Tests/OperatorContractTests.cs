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
/// status, the scope of its declared write, and the package metadata both sides count and check the same way.
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
    public async Task Every_Production_Status_And_Scene_Value_The_Operator_Sends_Is_Accepted_By_The_Control_Planes_Worker_Validation()
    {
        var features = new FeaturesService(
            new FeatureCatalog(), _states, new InMemoryCredentialStore(), new NullFeatureActivator(), []);
        await features.SetWriteGrantAsync(FeatureCatalog.YouTubeId, granted: true, confirmWrites: true, CancellationToken.None);
        var service = new OperatorService(_store, new WriteGateService(new FeatureCatalog(), _states, _actions, _writes), _writes);
        Scene[] scenes =
        [
            new("s1", 1, "Rivers bend.", "A river from above", false, OperatorPipeline.Planned, null,
                OperatorPipeline.NoAudio, OperatorPipeline.TextCard, null, null, 0),
            new("s2", 2, "Water cuts the outer bank.", "An eroded bank", false, OperatorPipeline.Ready, null,
                OperatorPipeline.Generated, OperatorPipeline.Generated, "scene-s2.mp3", "scene-s2.png", 2400),
            new("s3", 3, "Floods move the bends.", "A flooded plain", false, OperatorPipeline.Ready, NarrationGenerator.NoSpeech,
                OperatorPipeline.NoAudio, OperatorPipeline.TextCard, null, null, 1600),
            new("s4", 4, "Subscribe.", "A map", false, OperatorPipeline.Failed, NarrationGenerator.Failed(4),
                OperatorPipeline.NoAudio, OperatorPipeline.TextCard, null, null, 0),
        ];
        string[] sent = [OperatorPipeline.Producing, OperatorPipeline.MediaReady];

        var saved = new List<OperatorProject>();
        foreach (var status in sent)
        {
            saved.Add(await service.SaveProjectFromWorkerAsync(
                new OperatorProject(string.Empty, "Why rivers meander", status, string.Empty, default, default, [], null, []) { Scenes = scenes },
                null,
                CancellationToken.None));
        }

        saved.Select(p => p.Status).ShouldBe(sent);
        saved.ShouldAllBe(p => p.Scenes.SequenceEqual(scenes));
    }

    [Fact]
    public async Task A_Scene_Save_From_A_Read_Older_Than_An_Owner_Reorder_Keeps_The_Owners_Order_And_After_A_Regenerate_Is_Refused_In_The_Words_The_Pipeline_Stops_On()
    {
        var features = new FeaturesService(
            new FeatureCatalog(), _states, new InMemoryCredentialStore(), new NullFeatureActivator(), []);
        await features.SetWriteGrantAsync(FeatureCatalog.YouTubeId, granted: true, confirmWrites: true, CancellationToken.None);
        var service = new OperatorService(_store, new WriteGateService(new FeatureCatalog(), _states, _actions, _writes), _writes);
        Scene Planned(string id, int order) => new(id, order, $"Narration {id}.", $"Picture {id}", false, OperatorPipeline.Planned, null,
            OperatorPipeline.NoAudio, OperatorPipeline.TextCard, null, null, 0);
        Scene Produced(Scene scene) => scene with
        {
            Status = OperatorPipeline.Ready,
            AudioSource = OperatorPipeline.Generated,
            VisualSource = OperatorPipeline.Generated,
            AudioFile = $"scene-{scene.Id}.mp3",
            VisualFile = $"scene-{scene.Id}.png",
            DurationMs = 800,
        };
        OperatorProject WithFirstProduced(OperatorProject read) =>
            read with { Scenes = [.. read.Scenes.Select(s => s.Id == "s1" ? Produced(s) : s)] };
        var created = await service.SaveProjectFromWorkerAsync(
            new OperatorProject(string.Empty, "Why rivers meander", OperatorPipeline.Producing, string.Empty, default, default, [], null, [])
            {
                Scenes = [Planned("s1", 1), Planned("s2", 2), Planned("s3", 3)],
            },
            null,
            CancellationToken.None);
        var beforeReorder = await service.GetProjectForWorkerAsync(created.Id, CancellationToken.None);

        await service.ReorderScenesAsync(created.Id, ["s3", "s1", "s2"], CancellationToken.None);
        var accepted = await service.SaveProjectFromWorkerAsync(WithFirstProduced(beforeReorder), null, CancellationToken.None);
        await service.RegenerateSceneAsync(created.Id, "s2", CancellationToken.None);
        var refused = await Should.ThrowAsync<OperatorTamperingException>(
            () => service.SaveProjectFromWorkerAsync(WithFirstProduced(beforeReorder), null, CancellationToken.None));
        var regenerated = await service.GetProjectForWorkerAsync(created.Id, CancellationToken.None);

        accepted.Scenes.OrderBy(s => s.Id).Select(s => (s.Id, s.Order, s.Status)).ShouldBe([
            ("s1", 2, OperatorPipeline.Ready), ("s2", 3, OperatorPipeline.Planned), ("s3", 1, OperatorPipeline.Planned),
        ]);
        refused.Message.ShouldBe(OperatorPipeline.NoLongerProducing);
        regenerated.Status.ShouldBe(OperatorService.ProductionQueued);
        regenerated.Scenes.ShouldBe(accepted.Scenes);
    }

    [Fact]
    public void The_Operators_Data_Folder_Scope_Is_The_Text_Of_The_Youtube_Write_Row_Of_The_Catalog()
    {
        var row = new FeatureCatalog().Find(FeatureCatalog.YouTubeId).ShouldNotBeNull().ScopeMatrix.ShouldNotBeNull()
            .Where(r => r.Access == ScopeAccess.Write)
            .ShouldHaveSingleItem();

        OperatorServiceCollectionExtensions.DataFolderScope.ShouldBe(row.Scope);
    }

    public static TheoryData<string[]> TagSets() => new()
    {
        Array.Empty<string>(),
        new[] { "rivers" },
        new[] { "rivers", "river bends", "oxbow lakes", "geography" },
        new[] { "a b c d", " ", "x" },
        Enumerable.Range(0, 30).Select(i => $"tag {i} with spaces").ToArray(),
    };

    [Theory]
    [MemberData(nameof(TagSets))]
    public void The_Operator_And_The_Control_Plane_Count_Tags_The_Same_Way_YouTube_Does(string[] tags)
        => PackageChecks.TagsLength(tags).ShouldBe(PackageRules.TagsLength(tags));

    [Theory]
    [InlineData("Why Rivers Bend", "Intro\n\n0:00 Erosion\n0:20 Floods", "rivers")]
    [InlineData("Amazing Geography", "0:00 Erosion", "rivers")]
    [InlineData("You will not believe this", "No list here.", "rivers")]
    [InlineData("Rivers", "Chapters 0:00 inline only", "why rivers bend")]
    public void The_Operator_And_The_Control_Plane_Check_Discoverability_The_Same_Way(string title, string description, string topic)
    {
        var worker = PackageChecks.Discoverability(title, description, topic, new OperatorMandate(
            "Teach", "Students", ["geography"], "weekly", "en", "calm", [], "views", "1000"));
        var controlPlane = PackageRules.WithMetadataChecks([], title, description, [], topic, ["geography"]).Single(c => c.Name == "discoverability");

        (controlPlane.Name, controlPlane.Result, controlPlane.Detail).ShouldBe((worker.Name, worker.Result, worker.Detail));
    }

    [Fact]
    public void A_First_Title_From_A_120_Character_Variant_Or_A_200_Character_Topic_Is_Metadata_The_Control_Plane_Accepts()
    {
        var script = new OperatorScript("Hook", [], "Subscribe", [string.Join(' ', Enumerable.Repeat("Sourdough", 12)) + " bread"], "How to bake.", ["bread"], []);
        var topic200 = string.Concat(Enumerable.Repeat("sourdough ", 19)) + "starter bread";

        PackageRules.MetadataErrors(PackageChecks.Title(script, "sourdough"), "How to bake.", ["bread"]).ShouldBeEmpty();
        PackageRules.MetadataErrors(PackageChecks.Title(script with { TitleVariants = [] }, topic200), "How to bake.", ["bread"]).ShouldBeEmpty();
    }
}
