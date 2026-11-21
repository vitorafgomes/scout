using System.Text.Json.Nodes;
using Scout.Review;

namespace Scout.Audit.Tests;

/// <summary>
/// What the job's reader of a batch's findings block takes, with the one validation of Scout.Review, over the table of
/// inputs the plugin's reader and the control plane's gate are pinned with too, and the sentence it keeps on what it left out.
/// </summary>
public sealed class FindingsBlockTests
{
    private const string Commit = "0123456789abcdef0123456789abcdef01234567";

    [Theory]
    [InlineData("valid", 1, null)]
    [InlineData("id-with-space", 0, "1 finding left out: it needs an id of 1 to 40 letters, digits or hyphens.")]
    [InlineData("id-41", 0, "1 finding left out: it needs an id of 1 to 40 letters, digits or hyphens.")]
    [InlineData("severity-high", 0, "1 finding left out: it needs a severity of block, fix or nit.")]
    [InlineData("title-blank", 0, "1 finding left out: it needs a title of 1 to 200 characters.")]
    [InlineData("title-201", 0, "1 finding left out: it needs a title of 1 to 200 characters.")]
    [InlineData("title-missing", 0, "1 finding left out: it needs a title of 1 to 200 characters.")]
    [InlineData("path-dotdot", 0, "1 finding left out: it needs a path relative to the repository root, without a leading slash or '..'.")]
    [InlineData("path-leading-slash", 0, "1 finding left out: it needs a path relative to the repository root, without a leading slash or '..'.")]
    [InlineData("path-1001", 0, "1 finding left out: it needs a path relative to the repository root, without a leading slash or '..'.")]
    [InlineData("line-0", 0, "1 finding left out: it needs a line that is a positive whole number.")]
    [InlineData("why-blank", 0, "1 finding left out: it needs a why that says what is wrong.")]
    [InlineData("fix-missing", 0, "1 finding left out: it needs a fix.")]
    [InlineData("why-4001", 0, "1 finding left out: it has a why of 4001 characters; at most 4000 are allowed.")]
    [InlineData("fix-4001", 0, "1 finding left out: it has a fix of 4001 characters; at most 4000 are allowed.")]
    [InlineData("context-4001", 0, "1 finding left out: it has a context of 4001 characters; at most 4000 are allowed.")]
    [InlineData("rule-301", 0, "1 finding left out: it needs a rule of 1 to 300 characters, or none.")]
    [InlineData("ids-repeated", 2, null)]
    [InlineData("findings-51", 51, null)]
    public void The_Job_s_Reader_Takes_What_The_Pinned_Table_Records_And_Says_What_It_Left_Out(string row, int taken, string? leftOut)
    {
        var block = "```scout-findings\n{\"repository\":\"acme/shop\",\"number\":12,\"commit\":\"" + Commit
            + "\",\"verdict\":\"comment\",\"findings\":" + PinnedFindings.Json(row) + "}\n```";

        var (_, findings, dropped, accepted, left) = FindingsBlock.Read([block], "acme", "shop", Commit, 12);

        findings.Count.ShouldBe(taken, $"the findings the job's reader takes from {row}");
        left?.Sentence.ShouldBe(leftOut, $"the sentence on what the job's reader left out of {row}");
        (left is null).ShouldBe(leftOut is null, $"the job's reader left something out of {row} exactly when the table says so");
        (dropped, accepted).ShouldBe((0, true), $"the block of {row} is the job's and is accepted, nothing counted as dropped");
    }

    [Fact]
    public void A_Finding_With_No_Why_Or_No_Fix_Is_Left_Out_And_Counted()
    {
        var block = "```scout-findings\n{\"repository\":\"acme/shop\",\"number\":12,\"commit\":\"" + Commit
            + "\",\"verdict\":\"comment\",\"findings\":[{\"id\":\"a\",\"severity\":\"fix\",\"title\":\"T\",\"path\":\"src/A.cs\",\"line\":3,\"fix\":\"F\"},"
            + "{\"id\":\"b\",\"severity\":\"fix\",\"title\":\"U\",\"path\":\"src/A.cs\",\"line\":4,\"why\":\"W\"},"
            + "{\"id\":\"c\",\"severity\":\"nit\",\"title\":\"V\",\"path\":\"src/A.cs\",\"line\":5,\"why\":\"W\",\"fix\":\"F\"}]}\n```";

        var (_, findings, _, accepted, leftOut) = FindingsBlock.Read([block], "acme", "shop", Commit, 12);

        findings.Select(f => f.Id).ShouldBe(["c"], "a finding with no why or no fix is left out");
        (accepted, leftOut?.Sentence).ShouldBe(
            (true, "2 findings left out: the first needs a why that says what is wrong."),
            "the batch is still read, and the sentence counts what was left out and names the first problem");
    }

    [Fact]
    public void A_Findings_Block_Written_In_Today_s_Shape_Is_Written_And_Read_The_Same()
    {
        const string Today = "```scout-findings\n{\"kind\":\"audit\",\"repository\":\"acme/shop\",\"commit\":\"" + Commit
            + "\",\"verdict\":\"sound\",\"read\":\"2 of 2 files read\",\"findings\":[{\"id\":\"a\",\"severity\":\"block\",\"title\":\"T\","
            + "\"path\":\"src/A.cs\",\"line\":3,\"context\":\"C\",\"why\":\"W\",\"fix\":\"F\",\"rule\":\"R\"}]}\n```";
        var finding = new Finding("a", "block", "T", "src/A.cs", 3, "C", "W", "F", "R");

        var written = FindingsBlock.Write("acme", "shop", Commit, "sound", "2 of 2 files read", [finding]);
        var (verdict, read, _, _, _) = FindingsBlock.Read([Today], "acme", "shop", Commit);

        written.ShouldBe(Today, "a job's findings are written with the same field names in the same order as today");
        (verdict, read.ShouldHaveSingleItem()).ShouldBe(("sound", finding), "a block written in today's shape is read back field by field");
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
