namespace Scout.Review.Tests;

/// <summary>
/// The one validation of a finding the plugin, the job and the control plane use: the first problem in words, or none;
/// why, fix and context are at most 4,000 characters each, and a finding needs a why and a fix.
/// </summary>
public sealed class FindingValidationTests
{
    private const string Id = "needs an id of 1 to 40 letters, digits or hyphens.";
    private const string Title = "needs a title of 1 to 200 characters.";
    private const string Path = "needs a path relative to the repository root, without a leading slash or '..'.";

    private static readonly Finding Valid = new(
        "a", "fix", "Cart total skips the discount", "src/A.cs", 3, null, "The total ignores the discount.", "Apply the discount first.", null);

    public static TheoryData<string, Finding, string?> Findings() => new()
    {
        { "valid", Valid, null },
        { "id with a space", Valid with { Id = "a b" }, Id },
        { "id of 41", Valid with { Id = new string('a', 41) }, Id },
        { "id of 40", Valid with { Id = new string('a', 40) }, null },
        { "no id", Valid with { Id = null! }, Id },
        { "severity high", Valid with { Severity = "high" }, "needs a severity of block, fix or nit." },
        { "blank title", Valid with { Title = "  " }, Title },
        { "no title", Valid with { Title = null! }, Title },
        { "title of 201", Valid with { Title = new string('t', 201) }, Title },
        { "title of 200", Valid with { Title = new string('t', 200) }, null },
        { "path with ..", Valid with { Path = "src/../A.cs" }, Path },
        { "path from the root", Valid with { Path = "/src/A.cs" }, Path },
        { "path with .. inside a name", Valid with { Path = "src/a..b.cs" }, Path },
        { "path with a backslash", Valid with { Path = "src\\A.cs" }, Path },
        { "path with an empty segment", Valid with { Path = "src//A.cs" }, Path },
        { "no path", Valid with { Path = null! }, Path },
        { "path of 1001", Valid with { Path = "src/" + new string('a', 997) }, Path },
        { "path of 1000", Valid with { Path = "src/" + new string('a', 996) }, null },
        { "line 0", Valid with { Line = 0 }, "needs a line that is a positive whole number." },
        { "blank why", Valid with { Why = "" }, "needs a why that says what is wrong." },
        { "no why", Valid with { Why = null! }, "needs a why that says what is wrong." },
        { "no fix", Valid with { Fix = null! }, "needs a fix." },
        { "blank fix", Valid with { Fix = " " }, "needs a fix." },
        { "why of 4001", Valid with { Why = new string('w', 4001) }, "has a why of 4001 characters; at most 4000 are allowed." },
        { "why of 4000", Valid with { Why = new string('w', 4000) }, null },
        { "fix of 4001", Valid with { Fix = new string('f', 4001) }, "has a fix of 4001 characters; at most 4000 are allowed." },
        { "context of 4001", Valid with { Context = new string('c', 4001) }, "has a context of 4001 characters; at most 4000 are allowed." },
        { "context of 4000", Valid with { Context = new string('c', 4000) }, null },
        { "rule of 301", Valid with { Rule = new string('r', 301) }, "needs a rule of 1 to 300 characters, or none." },
        { "rule of 300", Valid with { Rule = new string('r', 300) }, null },
        { "blank rule", Valid with { Rule = "  " }, null },
        { "evidence of 1001", Valid with { Evidence = new string('e', 1001) }, "has evidence of 1001 characters; at most 1000 are allowed." },
        { "evidence of 1000", Valid with { Evidence = new string('e', 1000) }, null },
        { "no evidence", Valid with { Evidence = null }, null },
    };

    [Theory]
    [MemberData(nameof(Findings))]
    public void A_Finding_Is_Answered_With_Its_First_Problem_In_Words(string row, Finding finding, string? problem) =>
        FindingValidation.ProblemOf(finding).ShouldBe(problem, $"the one validation's answer to a finding with {row}");

    [Fact]
    public void A_Text_Over_4000_Characters_Is_Refused_Naming_The_Field_And_The_Limit()
    {
        string?[] problems =
        [
            FindingValidation.ProblemOf(Valid with { Why = new string('w', 4001) }),
            FindingValidation.ProblemOf(Valid with { Fix = new string('f', 4001) }),
            FindingValidation.ProblemOf(Valid with { Context = new string('c', 4001) }),
        ];

        problems.ShouldBe(
            [
                "has a why of 4001 characters; at most 4000 are allowed.",
                "has a fix of 4001 characters; at most 4000 are allowed.",
                "has a context of 4001 characters; at most 4000 are allowed.",
            ],
            "a why, a fix or a context over 4,000 characters is refused, naming the field and the limit");
    }

    [Fact]
    public void The_Findings_Are_At_Most_50_Each_Valid_And_With_An_Id_Of_Its_Own()
    {
        var fifty = Enumerable.Range(1, 50).Select(n => Valid with { Id = $"f{n}" }).ToList();

        string?[] problems =
        [
            FindingValidation.ProblemOf([]),
            FindingValidation.ProblemOf(fifty),
            FindingValidation.ProblemOf([.. fifty, Valid with { Id = "f51" }]),
            FindingValidation.ProblemOf([Valid, Valid with { Title = "Another" }]),
            FindingValidation.ProblemOf([Valid, Valid with { Id = "b", Fix = null! }]),
            FindingValidation.ProblemOf([null!]),
        ];

        problems.ShouldBe(
            [
                null,
                null,
                "There are 51 findings; at most 50 are allowed.",
                "Finding 2 repeats the id a; each id must be unique.",
                "Finding 2 needs a fix.",
                "Finding 1 is missing.",
            ],
            "none or up to 50 findings pass; the 51st, a repeated id, an invalid finding or a missing one is named by its position");
    }

    [Fact]
    public void The_Severities_Are_Block_Fix_And_Nit_And_Only_Block_Is_Blocking()
    {
        string?[] severities = ["block", "fix", "nit", "high", null];

        Severities.All.ShouldBe(["block", "fix", "nit"], "the severities are block, fix and nit");
        severities.Select(Severities.IsKnown).ShouldBe([true, true, true, false, false], "only block, fix and nit are severities");
        severities.Select(Severities.IsBlocking).ShouldBe([true, false, false, false, false], "blocking means block");
    }
}
