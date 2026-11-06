using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.SemanticKernel;
using Scout.Llm.Abstractions;
using Scout.Plugin.GitHub.Abstractions;

namespace Scout.Plugin.GitHub.Tests;

public sealed partial class GitHubPluginFindingsTests
{
    private const string Recorded = "Findings recorded: 2. End your answer with the verdict; the cards show the findings.";

    private const string TwoFindings = """
        [{"id":"total","severity":"block","title":"Discounts applied after the sum","path":"src/orders/Checkout.cs","line":88,"context":"","why":"`total` is summed before the discount loop runs.","fix":"Apply the discounts before the sum."},
         {"id":"swallow","severity":"nit","title":"Swallowed gateway error","path":"src/orders/Gateway.cs","line":12,"context":"Not from this PR.","why":"The catch returns an empty list.","fix":"Let it fail."}]
        """;

    [GeneratedRegex(@"```scout-findings[ \t]*\r?\n([\s\S]*?)\r?\n```")]
    private static partial Regex RendererBlock();

    private static async Task<string> PresentAsync(
        string findings, string repository = "octocat/hello", int number = 5, string verdict = "needs rework", string? commit = "ec7f951", string? read = "2 of 2 files read")
    {
        var plugin = KernelPluginFactory.CreateFromObject(new GitHubPlugin(Substitute.For<IGitHubClient>()), "GitHub");
        var arguments = new KernelArguments
        {
            ["repository"] = repository,
            ["number"] = number,
            ["verdict"] = verdict,
            ["findings_json"] = findings,
        };
        if (commit is not null)
        {
            arguments["commit"] = commit;
        }

        if (read is not null)
        {
            arguments["read"] = read;
        }

        var result = await plugin["present_review_findings"].InvokeAsync(new Kernel(), arguments);
        return result.GetValue<string>()!;
    }

    private static JsonElement Review(string block)
    {
        var match = RendererBlock().Matches(block).ShouldHaveSingleItem();
        match.Index.ShouldBe(0);
        match.Length.ShouldBe(block.Length);
        return JsonDocument.Parse(match.Groups[1].Value).RootElement.Clone();
    }

    [Fact]
    public async Task A_Valid_Call_Attaches_One_Findings_Block_And_Answers_The_Recorded_Sentence()
    {
        using var turn = TurnAttachments.Begin();

        var answer = await PresentAsync(TwoFindings);

        answer.ShouldBe(Recorded);
        var review = Review(TurnAttachments.Take().ShouldHaveSingleItem());
        review.GetProperty("repository").GetString().ShouldBe("octocat/hello");
        review.GetProperty("number").GetInt32().ShouldBe(5);
        review.GetProperty("commit").GetString().ShouldBe("ec7f951");
        review.GetProperty("verdict").GetString().ShouldBe("needs rework");
        review.GetProperty("read").GetString().ShouldBe("2 of 2 files read");
        var findings = review.GetProperty("findings").EnumerateArray().ToArray();
        findings.Select(f => f.GetProperty("id").GetString()).ShouldBe(["total", "swallow"]);
        findings.Select(f => f.GetProperty("severity").GetString()).ShouldBe(["block", "nit"]);
        findings.Select(f => f.GetProperty("title").GetString()).ShouldBe(["Discounts applied after the sum", "Swallowed gateway error"]);
        findings.Select(f => f.GetProperty("path").GetString()).ShouldBe(["src/orders/Checkout.cs", "src/orders/Gateway.cs"]);
        findings.Select(f => f.GetProperty("line").GetInt32()).ShouldBe([88, 12]);
        findings.Select(f => f.GetProperty("why").GetString()).ShouldBe(["`total` is summed before the discount loop runs.", "The catch returns an empty list."]);
        findings.Select(f => f.GetProperty("fix").GetString()).ShouldBe(["Apply the discounts before the sum.", "Let it fail."]);
        findings[0].TryGetProperty("context", out _).ShouldBeFalse();
        findings[1].GetProperty("context").GetString().ShouldBe("Not from this PR.");
    }

    [Fact]
    public async Task A_Call_Without_Commit_Or_Read_Leaves_Them_Out_Of_The_Block_Instead_Of_Writing_Null()
    {
        using var turn = TurnAttachments.Begin();

        (await PresentAsync(TwoFindings, commit: null, read: null)).ShouldBe(Recorded);

        var block = TurnAttachments.Take().ShouldHaveSingleItem();
        block.ShouldNotContain("null");
        var review = Review(block);
        review.TryGetProperty("commit", out _).ShouldBeFalse();
        review.TryGetProperty("read", out _).ShouldBeFalse();
    }

    public static TheoryData<string, int, string, string, string> InvalidShapes()
    {
        const string Valid = """{"id":"a","severity":"fix","title":"T","path":"src/a.cs","line":3,"why":"W","fix":"F"}""";
        static string With(string field, string value) =>
            "[" + JsonSerializer.Serialize(
                JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(Valid)!
                    .Where(p => p.Key != field)
                    .Concat(value.Length == 0 ? [] : [new KeyValuePair<string, JsonElement>(field, JsonDocument.Parse(value).RootElement.Clone())])
                    .ToDictionary()) + "]";

        var fiftyOne = "[" + string.Join(",", Enumerable.Range(1, 51).Select(i => Valid.Replace("\"a\"", $"\"f{i}\"", StringComparison.Ordinal))) + "]";
        return new()
        {
            { "octocat", 5, "ok", $"[{Valid}]", ReviewFindings.InvalidRepository },
            { "octocat/../hello", 5, "ok", $"[{Valid}]", ReviewFindings.InvalidRepository },
            { "octocat/hello", 0, "ok", $"[{Valid}]", ReviewFindings.InvalidNumber },
            { "octocat/hello", 5, " ", $"[{Valid}]", ReviewFindings.InvalidVerdict },
            { "octocat/hello", 5, "ok", "not json", ReviewFindings.InvalidFindings },
            { "octocat/hello", 5, "ok", Valid, ReviewFindings.InvalidFindings },
            { "octocat/hello", 5, "ok", "[]", ReviewFindings.InvalidFindings },
            { "octocat/hello", 5, "ok", fiftyOne, ReviewFindings.InvalidFindings },
            { "octocat/hello", 5, "ok", "[\"a\"]", "Finding 1 must be a JSON object." },
            { "octocat/hello", 5, "ok", With("id", "\"a b\""), "Finding 1 needs an id of 1 to 40 letters, digits or hyphens." },
            { "octocat/hello", 5, "ok", With("id", "\"" + new string('a', 41) + "\""), "Finding 1 needs an id of 1 to 40 letters, digits or hyphens." },
            { "octocat/hello", 5, "ok", $"[{Valid},{Valid}]", "Finding 2 repeats the id a; each id must be unique." },
            { "octocat/hello", 5, "ok", With("severity", "\"major\""), "Finding 1 needs a severity of block, fix or nit." },
            { "octocat/hello", 5, "ok", With("title", "\" \""), "Finding 1 needs a title of 1 to 200 characters." },
            { "octocat/hello", 5, "ok", With("title", "\"" + new string('t', 201) + "\""), "Finding 1 needs a title of 1 to 200 characters." },
            { "octocat/hello", 5, "ok", With("path", "\"src/../secret.cs\""), "Finding 1 needs a path relative to the repository root, without a leading slash or '..'." },
            { "octocat/hello", 5, "ok", With("path", "\"src/a..b.cs\""), "Finding 1 needs a path relative to the repository root, without a leading slash or '..'." },
            { "octocat/hello", 5, "ok", With("path", "\"/etc/passwd\""), "Finding 1 needs a path relative to the repository root, without a leading slash or '..'." },
            { "octocat/hello", 5, "ok", With("line", "0"), "Finding 1 needs a line that is a positive whole number." },
            { "octocat/hello", 5, "ok", With("line", "\"12\""), "Finding 1 needs a line that is a positive whole number." },
            { "octocat/hello", 5, "ok", With("line", "1.5"), "Finding 1 needs a line that is a positive whole number." },
            { "octocat/hello", 5, "ok", With("context", "7"), "Finding 1 has a context that is not text." },
            { "octocat/hello", 5, "ok", With("why", ""), "Finding 1 needs a why that says what is wrong." },
            { "octocat/hello", 5, "ok", With("fix", "\"\""), "Finding 1 needs a fix." },
        };
    }

    [Theory]
    [MemberData(nameof(InvalidShapes))]
    public async Task Each_Invalid_Shape_Answers_Its_Sentence_And_Attaches_Nothing(
        string repository, int number, string verdict, string findings, string sentence)
    {
        using var turn = TurnAttachments.Begin();

        var answer = await PresentAsync(findings, repository, number, verdict);

        answer.ShouldBe(sentence);
        TurnAttachments.Take().ShouldBeEmpty();
    }

    [Fact]
    public async Task Findings_Over_24000_Characters_As_A_Block_Are_Refused_With_Send_Fewer_And_Attach_Nothing()
    {
        using var turn = TurnAttachments.Begin();
        static string Findings(int count, int whyLength) => "[" + string.Join(",", Enumerable.Range(1, count).Select(i =>
            $$"""{"id":"f{{i}}","severity":"fix","title":"T","path":"src/a.cs","line":{{i}},"why":"{{new string('w', whyLength)}}","fix":"F"}""")) + "]";

        (await PresentAsync(Findings(16, 1_500))).ShouldBe(
            "The findings are over 24,000 characters as a block; send fewer findings, the ten that matter most, with shorter why and fix texts.");
        TurnAttachments.Take().ShouldBeEmpty();

        (await PresentAsync(Findings(10, 1_500))).ShouldStartWith("Findings recorded: 10.");
        TurnAttachments.Take().ShouldHaveSingleItem().Length.ShouldBeLessThanOrEqualTo(24_000);
    }

    [Fact]
    public async Task A_Second_Call_In_The_Turn_Replaces_The_First_Block()
    {
        using var turn = TurnAttachments.Begin();
        const string One = """[{"id":"only","severity":"fix","title":"Revised","path":"src/a.cs","line":3,"why":"W","fix":"F"}]""";

        await PresentAsync(TwoFindings);
        var answer = await PresentAsync(One, verdict: "ready after the fix");

        answer.ShouldBe("Findings recorded: 1. End your answer with the verdict; the cards show the findings.");
        var review = Review(TurnAttachments.Take().ShouldHaveSingleItem());
        review.GetProperty("verdict").GetString().ShouldBe("ready after the fix");
        review.GetProperty("findings").EnumerateArray().Select(f => f.GetProperty("id").GetString()).ShouldBe(["only"]);
    }
}
