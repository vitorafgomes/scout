using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.SemanticKernel;
using Scout.Llm.Abstractions;
using Scout.Plugin.GitHub.Abstractions;
using Scout.Review;

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

    [Theory]
    [InlineData(nameof(GitHubPlugin.PresentReviewFindingsAsync))]
    [InlineData(nameof(GitHubPlugin.PresentAuditFindings))]
    public void The_Model_Is_Told_The_Length_Limit_Of_Why_Fix_And_Context(string function)
    {
        var description = typeof(GitHubPlugin).GetMethod(function)!.GetParameters().Single(p => p.Name == "findings_json")
            .GetCustomAttributes(typeof(System.ComponentModel.DescriptionAttribute), false).Cast<System.ComponentModel.DescriptionAttribute>().Single().Description;

        description.ShouldContain(
            $"why, fix and context are at most {FindingLimits.MaxTextLength.ToString("N0", System.Globalization.CultureInfo.InvariantCulture)} characters each",
            Case.Sensitive,
            "the findings parameter tells the model the length limit the one validation applies");
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
    public async Task A_Finding_s_Evidence_Is_Read_And_Written_In_The_Block_And_A_Finding_Without_One_Carries_None()
    {
        using var turn = TurnAttachments.Begin();
        const string Findings = """[{"id":"a","severity":"fix","title":"T","path":"src/a.cs","line":3,"why":"W","fix":"F","evidence":"    var total = 0m;\r\n    total += line.Price;"},{"id":"b","severity":"nit","title":"U","path":"src/b.cs","line":4,"why":"W","fix":"F"}]""";

        (await PresentAsync(Findings)).ShouldBe(Recorded);

        var findings = Review(TurnAttachments.Take().ShouldHaveSingleItem()).GetProperty("findings").EnumerateArray().ToArray();
        findings[0].GetProperty("evidence").GetString().ShouldBe("    var total = 0m;\r\n    total += line.Price;", "the evidence is carried exactly as given, spacing and line ends included");
        findings[1].TryGetProperty("evidence", out _).ShouldBeFalse("a finding with no evidence carries no evidence field");
    }

    [Fact]
    public void The_Review_Function_Tells_The_Model_About_Evidence()
    {
        var parameter = typeof(GitHubPlugin).GetMethod(nameof(GitHubPlugin.PresentReviewFindingsAsync))!.GetParameters().Single(p => p.Name == "findings_json");
        var description = parameter.GetCustomAttributes(typeof(System.ComponentModel.DescriptionAttribute), false).Cast<System.ComponentModel.DescriptionAttribute>().Single().Description;

        description.ShouldContain(
            "evidence is the exact quote of the new side of the file's patch the finding is about, or of its removed lines without the leading '-' for a finding about a removal, contiguous lines with their spacing; Scout derives the line from it",
            Case.Sensitive,
            "the model is told what evidence is for a review");
    }

    [Fact]
    public async Task A_Call_Without_Read_Leaves_It_Out_Of_The_Block_Instead_Of_Writing_Null()
    {
        using var turn = TurnAttachments.Begin();

        (await PresentAsync(TwoFindings, read: null)).ShouldBe(Recorded);

        var block = TurnAttachments.Take().ShouldHaveSingleItem();
        block.ShouldNotContain("null");
        Review(block).TryGetProperty("read", out _).ShouldBeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public async Task A_Review_Without_The_Commit_It_Read_Is_Refused_And_Attaches_Nothing(string? commit)
    {
        using var turn = TurnAttachments.Begin();

        (await PresentAsync(TwoFindings, commit: commit)).ShouldBe(
            "The review needs the head commit it read, the SHA get_pull_request answered; the card pins the verdict to it.");

        TurnAttachments.Take().ShouldBeEmpty();
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
            { "octocat/hello", 5, "ok", "not json", ReviewFindings.InvalidReviewFindings },
            { "octocat/hello", 5, "ok", Valid, ReviewFindings.InvalidReviewFindings },
            { "octocat/hello", 5, "ok", fiftyOne, "There are 51 findings; at most 50 are allowed." },
            { "octocat/hello", 5, "ok", "[\"a\"]", "Finding 1 must be a JSON object." },
            { "octocat/hello", 5, "ok", With("id", "\"a b\""), "Finding 1 needs an id of 1 to 40 letters, digits or hyphens." },
            { "octocat/hello", 5, "ok", With("id", "\"" + new string('a', 41) + "\""), "Finding 1 needs an id of 1 to 40 letters, digits or hyphens." },
            { "octocat/hello", 5, "ok", With("id", "\"a1\\n\""), "Finding 1 needs an id of 1 to 40 letters, digits or hyphens." },
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
            { "octocat/hello", 5, "ok", With("evidence", "42"), "Finding 1 has evidence that is not text." },
            { "octocat/hello", 5, "ok", With("evidence", "{}"), "Finding 1 has evidence that is not text." },
            { "octocat/hello", 5, "ok", With("evidence", "[]"), "Finding 1 has evidence that is not text." },
            { "octocat/hello", 5, "ok", With("why", ""), "Finding 1 needs a why that says what is wrong." },
            { "octocat/hello", 5, "ok", With("fix", "\"\""), "Finding 1 needs a fix." },
        };
    }

    [Theory]
    [InlineData("null")]
    [InlineData("\"var q = Find(name);\"")]
    public async Task Evidence_That_Is_Null_Or_Text_Is_No_Type_Problem(string evidence)
    {
        using var turn = TurnAttachments.Begin();
        var finding = """{"id":"a","severity":"fix","title":"T","path":"src/a.cs","line":3,"why":"W","fix":"F","evidence":""" + evidence + "}";

        var answer = await PresentAsync($"[{finding}]");

        answer.ShouldNotContain("not text", Case.Sensitive, $"evidence {evidence} is null or text, so it is not refused for its type");
        answer.ShouldStartWith("Findings recorded:", Case.Sensitive, $"a finding with evidence {evidence} is recorded");
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

    private static async Task<string> AuditAsync(
        string findings, string repository = "octocat/hello", string verdict = "needs rework", string? commit = "ec7f951", string? read = "the whole repository", string? known = null)
    {
        var plugin = KernelPluginFactory.CreateFromObject(new GitHubPlugin(Substitute.For<IGitHubClient>()), "GitHub");
        var arguments = new KernelArguments
        {
            ["repository"] = repository,
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

        if (known is not null)
        {
            arguments["known_json"] = known;
        }

        var result = await plugin["present_audit_findings"].InvokeAsync(new Kernel(), arguments);
        return result.GetValue<string>()!;
    }

    [Fact]
    public async Task An_Audit_Call_Attaches_One_Audit_Block_Without_A_Number_And_Answers_The_Recorded_Sentence()
    {
        using var turn = TurnAttachments.Begin();

        var answer = await AuditAsync(TwoFindings, known: """["No paging on the orders list, noted in the README."]""");

        answer.ShouldBe(Recorded);
        var block = TurnAttachments.Take().ShouldHaveSingleItem();
        block.ShouldStartWith("```scout-findings\n{\"kind\":\"audit\",\"repository\":\"octocat/hello\",");
        var audit = Review(block);
        audit.GetProperty("kind").GetString().ShouldBe("audit");
        audit.TryGetProperty("number", out _).ShouldBeFalse();
        audit.GetProperty("repository").GetString().ShouldBe("octocat/hello");
        audit.GetProperty("commit").GetString().ShouldBe("ec7f951");
        audit.GetProperty("verdict").GetString().ShouldBe("needs rework");
        audit.GetProperty("read").GetString().ShouldBe("the whole repository");
        audit.TryGetProperty("known", out var known).ShouldBeTrue();
        known.EnumerateArray().Select(k => k.GetString()).ShouldBe(["No paging on the orders list, noted in the README."]);
        var findings = audit.GetProperty("findings").EnumerateArray().ToArray();
        findings.Select(f => f.GetProperty("id").GetString()).ShouldBe(["total", "swallow"]);
        findings.Select(f => f.GetProperty("severity").GetString()).ShouldBe(["block", "nit"]);
        findings.Select(f => f.GetProperty("path").GetString()).ShouldBe(["src/orders/Checkout.cs", "src/orders/Gateway.cs"]);
        findings.Select(f => f.GetProperty("line").GetInt32()).ShouldBe([88, 12]);
        findings.Select(f => f.GetProperty("fix").GetString()).ShouldBe(["Apply the discounts before the sum.", "Let it fail."]);
    }

    public static TheoryData<string, string, string, string?, string> InvalidAuditShapes()
    {
        const string Valid = """{"id":"a","severity":"fix","title":"T","path":"src/a.cs","line":3,"why":"W","fix":"F"}""";
        static string WithRule(string rule) => "[" + Valid.Replace("}", ",\"rule\":" + rule + "}", StringComparison.Ordinal) + "]";
        static string Known(int count, int length) =>
            JsonSerializer.Serialize(Enumerable.Range(1, count).Select(i => new string('k', length)).ToArray());

        var tooLong = "[" + string.Join(",", Enumerable.Range(1, 16).Select(i =>
            $$"""{"id":"f{{i}}","severity":"fix","title":"T","path":"src/a.cs","line":{{i}},"why":"{{new string('w', 1_500)}}","fix":"F"}""")) + "]";
        return new()
        {
            { "octocat", "ok", $"[{Valid}]", null, ReviewFindings.InvalidRepository },
            { "octocat/hello", " ", $"[{Valid}]", null, ReviewFindings.InvalidVerdict },
            { "octocat/hello", "ok", "[]", null, ReviewFindings.InvalidFindings },
            { "octocat/hello", "ok", Valid, null, ReviewFindings.InvalidFindings },
            { "octocat/hello", "ok", $"[{Valid},{Valid}]", null, "Finding 2 repeats the id a; each id must be unique." },
            { "octocat/hello", "ok", "[" + Valid.Replace("\"fix\"", "\"major\"", StringComparison.Ordinal) + "]", null, "Finding 1 needs a severity of block, fix or nit." },
            { "octocat/hello", "ok", "[" + Valid.Replace("src/a.cs", "../a.cs", StringComparison.Ordinal) + "]", null, "Finding 1 needs a path relative to the repository root, without a leading slash or '..'." },
            { "octocat/hello", "ok", WithRule("\"" + new string('r', 301) + "\""), null, "Finding 1 needs a rule of 1 to 300 characters, or none." },
            { "octocat/hello", "ok", WithRule("7"), null, "Finding 1 needs a rule of 1 to 300 characters, or none." },
            { "octocat/hello", "ok", $"[{Valid}]", Known(21, 10), ReviewFindings.InvalidKnown },
            { "octocat/hello", "ok", $"[{Valid}]", Known(1, 301), ReviewFindings.InvalidKnown },
            { "octocat/hello", "ok", $"[{Valid}]", "[\" \"]", ReviewFindings.InvalidKnown },
            { "octocat/hello", "ok", $"[{Valid}]", "[7]", ReviewFindings.InvalidKnown },
            { "octocat/hello", "ok", $"[{Valid}]", "\"one limitation\"", ReviewFindings.InvalidKnown },
            { "octocat/hello", "ok", $"[{Valid}]", "not json", ReviewFindings.InvalidKnown },
            { "octocat/hello", "ok", tooLong, null, GitHubPlugin.FindingsTooLong },
        };
    }

    [Theory]
    [MemberData(nameof(InvalidAuditShapes))]
    public async Task Each_Invalid_Audit_Shape_Answers_Its_Sentence_And_Attaches_Nothing(
        string repository, string verdict, string findings, string? known, string sentence)
    {
        using var turn = TurnAttachments.Begin();

        var answer = await AuditAsync(findings, repository, verdict, known: known);

        answer.ShouldBe(sentence);
        TurnAttachments.Take().ShouldBeEmpty();
    }

    private static string?[] Rules(JsonElement block) =>
        block.GetProperty("findings").EnumerateArray().Select(f => f.TryGetProperty("rule", out var rule) ? rule.GetString() : null).ToArray();

    [Fact]
    public async Task Rule_And_Known_Reach_The_Block_And_Absent_Ones_Are_Left_Out()
    {
        using var turn = TurnAttachments.Begin();
        const string Ruled = """
            [{"id":"a","severity":"fix","title":"T","path":"src/a.cs","line":3,"why":"W","fix":"F","rule":" ADR 0007: errors are results "},
             {"id":"b","severity":"nit","title":"T","path":"src/b.cs","line":4,"why":"W","fix":"F","rule":"  "},
             {"id":"c","severity":"nit","title":"T","path":"src/c.cs","line":5,"why":"W","fix":"F","rule":null},
             {"id":"d","severity":"nit","title":"T","path":"src/d.cs","line":6,"why":"W","fix":"F"}]
            """;

        (await AuditAsync(Ruled, known: """[" Retries are documented as missing. ","No paging."]""")).ShouldStartWith("Findings recorded: 4.");
        var audit = Review(TurnAttachments.Take().ShouldHaveSingleItem());
        audit.TryGetProperty("known", out var known).ShouldBeTrue();
        known.EnumerateArray().Select(k => k.GetString()).ShouldBe(["Retries are documented as missing.", "No paging."]);
        Rules(audit).ShouldBe(["ADR 0007: errors are results", null, null, null]);

        (await AuditAsync(Ruled, commit: null, read: null, known: "[]")).ShouldStartWith("Findings recorded: 4.");
        var bare = TurnAttachments.Take().ShouldHaveSingleItem();
        bare.ShouldNotContain("null");
        Review(bare).TryGetProperty("known", out _).ShouldBeFalse();

        (await PresentAsync(Ruled)).ShouldStartWith("Findings recorded: 4.");
        var review = Review(TurnAttachments.Take().ShouldHaveSingleItem());
        review.TryGetProperty("kind", out _).ShouldBeFalse();
        review.TryGetProperty("known", out _).ShouldBeFalse();
        review.GetProperty("number").GetInt32().ShouldBe(5);
        Rules(review).ShouldBe(["ADR 0007: errors are results", null, null, null]);
    }

    [Fact]
    public async Task An_Audit_Call_After_A_Review_Call_In_The_Turn_Replaces_The_Review_Block()
    {
        using var turn = TurnAttachments.Begin();

        await PresentAsync(TwoFindings);
        (await AuditAsync(TwoFindings)).ShouldBe(Recorded);

        Review(TurnAttachments.Take().ShouldHaveSingleItem()).GetProperty("kind").GetString().ShouldBe("audit");
    }

    [Fact]
    public async Task A_Review_With_No_Finding_Is_Recorded_And_An_Audit_Still_Needs_One()
    {
        using var turn = TurnAttachments.Begin();

        var review = await PresentAsync("[]", verdict: "ready to merge");

        review.ShouldBe("Findings recorded: 0. End your answer with the verdict; the cards show the findings.");
        var block = Review(TurnAttachments.Take().ShouldHaveSingleItem());
        block.GetProperty("number").GetInt32().ShouldBe(5);
        block.GetProperty("commit").GetString().ShouldBe("ec7f951");
        block.GetProperty("verdict").GetString().ShouldBe("ready to merge");
        block.GetProperty("read").GetString().ShouldBe("2 of 2 files read");
        var findings = block.GetProperty("findings");
        findings.ValueKind.ShouldBe(JsonValueKind.Array);
        findings.GetArrayLength().ShouldBe(0);

        (await AuditAsync("[]")).ShouldBe("The findings must be a JSON array of 1 to 50 findings.");
        TurnAttachments.Take().ShouldBeEmpty();
        (await PresentAsync("{}")).ShouldBe("The findings must be a JSON array of at most 50 findings; [] when the review found nothing.");
        TurnAttachments.Take().ShouldBeEmpty();
    }

    private static JsonElement OfferOf() => Review(TurnAttachments.Take().ShouldHaveSingleItem()).GetProperty("offer");

    [Theory]
    [InlineData("block", "2 of 2 files read", "request_changes", "Request changes: 1 finding needs fixing.", true)]
    [InlineData("nit", "2 of 2 files read", "comment", "Comment, not approve: 1 optional suggestion was kept, the pull request's checks could not be read and no test ran here and the pull request's checks cannot cover them.", false)]
    [InlineData("nit", "the whole diff", "comment", "Comment, not approve: 1 optional suggestion was kept, how many changed files were read is not known, the pull request's checks could not be read and no test ran here and the pull request's checks cannot cover them.", false)]
    public async Task The_single_turn_review_s_card_carries_the_offer_decided_by_code_never_the_model_s_verdict(string severity, string read, string verdict, string reason, bool commentInstead)
    {
        using var turn = TurnAttachments.Begin();

        await PresentAsync($$"""[{"id":"a","severity":"{{severity}}","title":"T","path":"src/A.cs","line":1,"why":"W.","fix":"F."}]""", verdict: "ready to merge", read: read);

        var offer = OfferOf();
        (offer.GetProperty("verdict").GetString(), offer.GetProperty("reason").GetString(), offer.GetProperty("commentInstead").GetBoolean()).ShouldBe(
            (verdict, reason, commentInstead), "the offer comes from the findings, the read count and the checks, whatever verdict the model wrote");
    }

    [Fact]
    public async Task A_first_reading_cannot_mark_its_own_finding_unconfirmed_to_lower_its_weight()
    {
        using var turn = TurnAttachments.Begin();

        await PresentAsync("""[{"id":"a","severity":"block","title":"unconfirmed:  Unconfirmed: Lookup is null","path":"src/A.cs","line":1,"why":"W.","fix":"F."}]""");

        var review = Review(TurnAttachments.Take().ShouldHaveSingleItem());
        review.GetProperty("findings")[0].GetProperty("title").GetString().ShouldBe("Lookup is null", "only the second reading may mark a finding unconfirmed");
        review.GetProperty("offer").GetProperty("verdict").GetString().ShouldBe("request_changes", "the finding keeps its weight");
    }
}
