using System.Text.RegularExpressions;
using Shouldly;
using Xunit;

namespace Scout.ArchTests;

/// <summary>
/// Rule J: a comment says what the code is, never where the rationale is filed. A path into the context folder, an
/// ADR number or an issue number in a comment is a link nothing checks and a rename breaks; the one
/// exception is a `ponytail:` deferral line, which names a ceiling, not a page.
/// </summary>
public partial class CommentReferenceTests
{
    private static readonly string[] BackendRoots = ["src", "tests"];
    private static readonly string[] UiRoots = ["ui/packages"];

    [GeneratedRegex(@"^[ \t]*(?:///?|\*)(?!.*ponytail:).*?(?:\bcontext/|\bpages/|\bADR[ -]?\d{4}\b|\bissue\s*#\d+\b|\bPR\s*#\d+\b)", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex CommentPointingElsewhere();

    internal static bool PointsElsewhere(string source) => CommentPointingElsewhere().IsMatch(SourceText.CommentText(source));

    private static IEnumerable<string> Offenders()
    {
        var files = SourceText.CSharpUnder(BackendRoots).Concat(SourceText.FilesNamed("*.ts", UiRoots));

        foreach (var file in files)
        {
            foreach (Match match in CommentPointingElsewhere().Matches(SourceText.CommentText(File.ReadAllText(file))))
            {
                yield return $"{SourceText.RepoPath(file)}: {match.Value.Trim()}";
            }
        }
    }

    [Fact]
    public void NoCommentPointsAtTheContextFolderAnAdrOrAnIssue()
    {
        var offenders = Offenders().OrderBy(x => x, StringComparer.Ordinal).ToList();

        offenders.ShouldBeEmpty(
            "a comment states the code fact; the rationale lives in the context folder, where a page can be revised "
          + "without a code change and a rename does not leave a dead pointer behind");
    }

    [Theory]
    [InlineData("    // see context/pages/conventions/testing.md", true)]
    [InlineData("    /// documented in pages/decisions/0004-postgresql.md", true)]
    [InlineData("    // ADR 0004 says one schema per module", true)]
    [InlineData("    /*\n     * closes issue #12\n     */", true)]
    [InlineData("    // the rationale is in the project context", false)]
    [InlineData("    // ponytail: global lock, per-tenant locks if throughput matters; context/pages/roadmap/followups.md", false)]
    [InlineData("    var path = \"context/pages/index.md\";", false)]
    [InlineData("    // fixes issue 2 of the three the review listed", false)]
    [InlineData("    var md = \"\"\"\n     * see context/pages/x.md\n    \"\"\";", false)]
    [InlineData("    /// <summary>Adds the Management module.</summary>", false)]
    public void TheDetectorSeparatesAPointerFromAStatement(string line, bool offends) =>
        PointsElsewhere(line).ShouldBe(offends);

    [Fact]
    public void TheRuleReadsBothTreesAtAll()
    {
        SourceText.CSharpUnder(BackendRoots).Count().ShouldBeGreaterThan(50, "the backend scan opened too few files");
        SourceText.FilesNamed("*.ts", UiRoots).Count().ShouldBeGreaterThan(20, "the ui scan opened too few files");
    }
}
