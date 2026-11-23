using System.Text.Json.Nodes;

namespace Scout.Plugin.GitHub.Tests;

/// <summary>
/// What the plugin's reader of a card's findings accepts and refuses, with the one validation of Scout.Review, over the table of inputs the job's reader and
/// the control plane's gate are pinned with too, so a change to any of the three shows against the others.
/// </summary>
public sealed class ReviewFindingsTests
{
    private const string Id = "Finding 1 needs an id of 1 to 40 letters, digits or hyphens.";
    private const string Title = "Finding 1 needs a title of 1 to 200 characters.";
    private const string Path = "Finding 1 needs a path relative to the repository root, without a leading slash or '..'.";

    [Theory]
    [InlineData("valid", null)]
    [InlineData("id-with-space", Id)]
    [InlineData("id-41", Id)]
    [InlineData("severity-high", "Finding 1 needs a severity of block, fix or nit.")]
    [InlineData("title-blank", Title)]
    [InlineData("title-201", Title)]
    [InlineData("title-missing", Title)]
    [InlineData("path-dotdot", Path)]
    [InlineData("path-leading-slash", Path)]
    [InlineData("path-1001", Path)]
    [InlineData("line-0", "Finding 1 needs a line that is a positive whole number.")]
    [InlineData("why-blank", "Finding 1 needs a why that says what is wrong.")]
    [InlineData("fix-missing", "Finding 1 needs a fix.")]
    [InlineData("why-4001", "Finding 1 has a why of 4001 characters; at most 4000 are allowed.")]
    [InlineData("fix-4001", "Finding 1 has a fix of 4001 characters; at most 4000 are allowed.")]
    [InlineData("context-4001", "Finding 1 has a context of 4001 characters; at most 4000 are allowed.")]
    [InlineData("rule-301", "Finding 1 needs a rule of 1 to 300 characters, or none.")]
    [InlineData("ids-repeated", "Finding 2 repeats the id a; each id must be unique.")]
    [InlineData("findings-51", "There are 51 findings; at most 50 are allowed.")]
    public void The_Plugin_s_Reader_Refuses_What_The_Pinned_Table_Records(string row, string? refused)
    {
        var (findings, problem) = ReviewFindings.ParseFindings(PinnedFindings.Json(row), ReviewFindings.InvalidReviewFindings);

        problem.ShouldBe(refused, $"the plugin's answer to {row}");
        (findings is null).ShouldBe(refused is not null, $"a refused {row} gives no findings and an accepted one gives them");
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
