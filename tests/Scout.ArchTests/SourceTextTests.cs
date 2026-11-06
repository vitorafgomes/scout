using Shouldly;
using Xunit;

namespace Scout.ArchTests;

/// <summary>The scanner every source rule stands on: what it blanks, what it keeps, and that length and newlines survive.</summary>
public class SourceTextTests
{
    private static void ShouldKeepShape(string source, string output)
    {
        output.Length.ShouldBe(source.Length);
        output.Count(c => c == '\n').ShouldBe(source.Count(c => c == '\n'));
    }

    [Theory]
    [InlineData("var s = \"if (!x)\"; // if (!y)", "!x,!y", "var s =,;")]
    [InlineData("var s = @\"qq\"\"zz\"; x!.Go();", "qq,zz", "x!.Go();")]
    [InlineData("var p = @$\"C:\\x \"\"{name}\"\" y\"; // see vault/pages/x.md !q", "name,vault", "var p =,; ")]
    [InlineData("var p = $@\"C:\\x \"\"{name}\"\" y\"; !q", "name", "var p =,; !q")]
    [InlineData("var s = \"\"\"\n[Fact(Skip = \"x\")]\n\"\"\"; !a", "Fact,Skip", "; !a")]
    [InlineData("var c = '\"'; /* !z */ !b", "!z", "var c =,!b")]
    [InlineData("var s = $\"\"\"\n  {x:N2} \"quoted\"\n  \"\"\"; !k", "N2,quoted", "!k")]
    public void StripBlanksLiteralsAndCommentsAndKeepsCode(string source, string gone, string kept)
    {
        var output = SourceText.StripLiteralsAndComments(source);

        ShouldKeepShape(source, output);
        foreach (var g in gone.Split(',')) { output.ShouldNotContain(g); }
        foreach (var k in kept.Split(',')) { output.ShouldContain(k); }
    }

    [Theory]
    [InlineData("var s = \"// vault/x\"; // see vault/y", "vault/x,var s", "// see vault/y")]
    [InlineData("code(); /* a\n b */ more();", "code,more", "/* a, b */")]
    [InlineData("var s = \"\"\"\n * see vault/pages/x.md\n\"\"\";", "vault,var s", "")]
    public void CommentsOnlyKeepsCommentsAndBlanksTheRest(string source, string gone, string kept)
    {
        var output = SourceText.CommentsOnly(source);

        ShouldKeepShape(source, output);
        foreach (var g in gone.Split(',')) { output.ShouldNotContain(g); }
        foreach (var k in kept.Split(',').Where(k => k.Length > 0)) { output.ShouldContain(k); }
    }

    [Fact]
    public void TheTestProjectsAreFound() =>
        SourceText.TestProjects().Count.ShouldBeGreaterThanOrEqualTo(5);
}
