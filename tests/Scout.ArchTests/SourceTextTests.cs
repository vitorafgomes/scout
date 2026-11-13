using Shouldly;
using Xunit;

namespace Scout.ArchTests;

/// <summary>
/// Pins the split every source rule relies on: each case names the parts of its input that must turn into spaces
/// (or the only parts that may survive), and the whole output is compared, so a line number never drifts.
/// </summary>
public class SourceTextTests
{
    /// <summary>The input with the named parts, found left to right, turned into spaces, or with everything else turned into spaces; newlines always stay.</summary>
    private static string Spaced(string input, bool spaceTheNamedParts, params string[] parts)
    {
        var named = new bool[input.Length];
        var searchFrom = 0;

        foreach (var part in parts)
        {
            var found = input.IndexOf(part, searchFrom, StringComparison.Ordinal);
            found.ShouldBeGreaterThanOrEqualTo(0, "a case names a part its input does not hold: " + part);
            Array.Fill(named, true, found, part.Length);
            searchFrom = found + part.Length;
        }

        return string.Concat(input.Select((c, i) => c == '\n' || named[i] != spaceTheNamedParts ? c : ' '));
    }

    [Theory]
    [InlineData("Run(\"a // b\"); // trailing note", "\"a // b\"", "// trailing note")]
    [InlineData("var t = @\"x \"\" y\"; t!.Trim();", "\"x \"\" y\"")]
    [InlineData("var u = @$\"D:\\temp \"\"{who}\"\"\"; // log", "\"D:\\temp \"\"{who}\"\"\"", "// log")]
    [InlineData("var u = $@\"D:\\temp \"\"{who}\"\"\"; !done", "\"D:\\temp \"\"{who}\"\"\"")]
    [InlineData("var raw = \"\"\"\n[Theory(Skip = \"y\")]\n\"\"\"; !tail", "\"\"\"\n[Theory(Skip = \"y\")]\n\"\"\"")]
    [InlineData("var ch = '\"'; /* note */ !after", "'\"'", "/* note */")]
    [InlineData("var raw = $\"\"\"\n  {price:F1} \"in quotes\"\n  \"\"\"; !end", "\"\"\"\n  {price:F1} \"in quotes\"\n  \"\"\"")]
    [InlineData("var raw = \"\"\"\"\n  holds \"\"\" inside\n  \"\"\"\"; !end", "\"\"\"\"\n  holds \"\"\" inside\n  \"\"\"\"")]
    [InlineData("var e = \"say \\\" more\"; !end", "\"say \\\" more\"")]
    [InlineData("Go(); /* never closed\n Stop();", "/* never closed\n Stop();")]
    public void CodeOnlyTurnsEveryLiteralAndCommentIntoSpaces(string input, params string[] literalsAndComments) =>
        SourceText.CodeOnly(input).ShouldBe(Spaced(input, spaceTheNamedParts: true, literalsAndComments));

    [Theory]
    [InlineData("var link = \"// not/a/note\"; // a real note", "// a real note")]
    [InlineData("Open(); /* one\n two */ Close();", "/* one\n two */")]
    [InlineData("var doc = \"\"\"\n * looks like a comment\n\"\"\";")]
    public void CommentTextKeepsOnlyTheComments(string input, params string[] comments) =>
        SourceText.CommentText(input).ShouldBe(Spaced(input, spaceTheNamedParts: false, comments));

    [Fact]
    public void TheProjectScanFindsTheTestSuites() =>
        SourceText.TestProjectFiles().Count.ShouldBeGreaterThanOrEqualTo(5);
}
