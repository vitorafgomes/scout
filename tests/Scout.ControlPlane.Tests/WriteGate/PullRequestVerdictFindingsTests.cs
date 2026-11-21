using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Scout.ControlPlane.Actions;
using Scout.ControlPlane.Features;
using Scout.ControlPlane.WriteGate;
using Shouldly;
using Xunit;

namespace Scout.ControlPlane.Tests.WriteGate;

/// <summary>
/// What the gate refuses of a verdict's findings today, over the table of inputs the plugin's reader and the job's reader
/// are pinned with too, so a change to any of the three shows against the others.
/// </summary>
public sealed class PullRequestVerdictFindingsTests : IDisposable
{
    private const string Sha = "0123456789abcdef0123456789abcdef01234567";
    private const string Every = "Every finding needs an id, a severity of block, fix or nit, a title, a path, a positive line, a why and a fix.";

    private readonly string _directory = Directory.CreateTempSubdirectory("scout-verdict-findings-").FullName;
    private readonly JsonFileFeatureStateStore _states;
    private readonly JsonFileActionQueue _actions;
    private readonly JsonLinesWriteLog _writes;

    public PullRequestVerdictFindingsTests()
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

    [Theory]
    [InlineData("valid", null)]
    [InlineData("id-with-space", null)]
    [InlineData("id-41", null)]
    [InlineData("severity-high", Every)]
    [InlineData("title-blank", Every)]
    [InlineData("title-201", null)]
    [InlineData("title-missing", Every)]
    [InlineData("path-dotdot", null)]
    [InlineData("path-leading-slash", null)]
    [InlineData("path-1001", null)]
    [InlineData("line-0", Every)]
    [InlineData("why-blank", Every)]
    [InlineData("fix-missing", Every)]
    [InlineData("why-4001", null)]
    [InlineData("fix-4001", null)]
    [InlineData("context-4001", null)]
    [InlineData("rule-301", null)]
    [InlineData("ids-repeated", PullRequestVerdictService.DuplicateFindingIds)]
    [InlineData("findings-51", "The findings must be a JSON array of at most 50 findings.")]
    public async Task The_Gate_Refuses_Today_What_The_Pinned_Table_Records(string row, string? refused)
    {
        await new FeaturesService(new FeatureCatalog(), _states, new InMemoryCredentialStore(), new NullFeatureActivator(), Array.Empty<IFeatureCredentialValidator>())
            .SetWriteGrantAsync(FeatureCatalog.GitHubId, granted: true, confirmWrites: true, CancellationToken.None);
        var findings = JsonSerializer.Deserialize<List<VerdictFinding>>(PinnedFindings.Json(row), JsonSerializerOptions.Web)!;
        var request = new PullRequestVerdictRequest("acme", "shop", 12, "comment", findings, Sha, null);

        string? problem = null;
        try
        {
            await new PullRequestVerdictService(_states, _actions, _writes).GiveAsync(request, CancellationToken.None);
        }
        catch (FeatureValidationFailedException refusal)
        {
            refusal.Errors.Keys.ShouldBe(["findings"], $"only the findings of {row} are refused, if anything");
            problem = refusal.Errors["findings"];
        }

        problem.ShouldBe(refused, $"the gate's answer to {row} today");
    }
}

/// <summary>The table of findings the three readers are pinned with: one JSON array per row.</summary>
internal static class PinnedFindings
{
    internal static string Json(string row)
    {
        JsonObject[] findings = row switch
        {
            "valid" => [Finding()],
            "id-with-space" => [Finding("id", "a b")],
            "id-41" => [Finding("id", new string('a', 41))],
            "severity-high" => [Finding("severity", "high")],
            "title-blank" => [Finding("title", "  ")],
            "title-201" => [Finding("title", new string('t', 201))],
            "title-missing" => [Without("title")],
            "path-dotdot" => [Finding("path", "src/../A.cs")],
            "path-leading-slash" => [Finding("path", "/src/A.cs")],
            "path-1001" => [Finding("path", "src/" + new string('a', 997))],
            "line-0" => [Finding("line", 0)],
            "why-blank" => [Finding("why", "")],
            "fix-missing" => [Without("fix")],
            "why-4001" => [Finding("why", new string('w', 4001))],
            "fix-4001" => [Finding("fix", new string('f', 4001))],
            "context-4001" => [Finding("context", new string('c', 4001))],
            "rule-301" => [Finding("rule", new string('r', 301))],
            "ids-repeated" => [Finding(), Finding("title", "another")],
            "findings-51" => [.. Enumerable.Range(1, 51).Select(n => Finding("id", $"f{n}"))],
            _ => throw new ArgumentOutOfRangeException(nameof(row), row, "No such row in the pinned table."),
        };
        return new JsonArray([.. findings]).ToJsonString();
    }

    private static JsonObject Finding(string? field = null, JsonNode? value = null)
    {
        var finding = new JsonObject
        {
            ["id"] = "a",
            ["severity"] = "fix",
            ["title"] = "Cart total skips the discount",
            ["path"] = "src/A.cs",
            ["line"] = 3,
            ["why"] = "The total ignores the discount.",
            ["fix"] = "Apply the discount first.",
        };
        if (field is not null)
        {
            finding[field] = value;
        }

        return finding;
    }

    private static JsonObject Without(string field)
    {
        var finding = Finding();
        finding.Remove(field);
        return finding;
    }
}
