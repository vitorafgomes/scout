namespace Scout.Review.Tests;

/// <summary>Where a finding's evidence, an exact quote of the new side of its file's patch, sits: the line Scout derives from it.</summary>
public sealed class DiffQuoteTests
{
    /// <summary>A patch whose new side is lines 10 to 16: a directive, a blank line, a comment and code.</summary>
    private const string Patch =
        "@@ -10,4 +10,7 @@ class Invoice\n" +
        " using System.Text;\n" +
        "+\n" +
        "+// The total of the lines.\n" +
        "+    var total = 0m;\n" +
        "-    total += 1;\n" +
        "+    total += line.Price;\n" +
        "     return total;\n" +
        "+    return total;";

    [Fact]
    public void The_new_side_of_a_patch_is_each_line_with_its_number_in_the_new_file()
    {
        DiffLines.RightSide(Patch).Select(l => (l.Line, l.Text)).ShouldBe(
            [
                (10, "using System.Text;"), (11, string.Empty), (12, "// The total of the lines."), (13, "    var total = 0m;"),
                (14, "    total += line.Price;"), (15, "    return total;"), (16, "    return total;"),
            ],
            "context and added lines, numbered in the new file, without their diff marker; removed lines are not on the new side");
        DiffLines.RightSideLines(Patch).Order().ToArray().ShouldBe([10, 11, 12, 13, 14, 15, 16], "the lines a finding may sit on are the same lines");
    }

    [Fact]
    public void A_quote_found_once_answers_its_line()
    {
        DiffQuote.Locate(Patch, "    total += line.Price;").ShouldBe(new QuoteMatch(14, null), "a single line quoted exactly sits on its line");
        DiffQuote.Locate(Patch, "    var total = 0m;\n    total += line.Price;").ShouldBe(new QuoteMatch(13, null), "contiguous lines sit on the first one's line");
    }

    [Fact]
    public void E3_CRLF_and_LF_match_and_so_does_a_difference_in_indentation()
    {
        DiffQuote.Locate(Patch, "    var total = 0m;\r\n    total += line.Price;\r\n").ShouldBe(new QuoteMatch(13, null), "CRLF and LF are the same line end");
        DiffQuote.Locate(Patch.Replace("\n", "\r\n", StringComparison.Ordinal), "    total += line.Price;").ShouldBe(new QuoteMatch(14, null), "a patch with CRLF reads the same");
        DiffQuote.Locate(Patch, "total += line.Price;").ShouldBe(new QuoteMatch(14, null), "a quote with other indentation is the same code");
    }

    [Fact]
    public void E4_a_quote_spanning_a_directive_a_blank_line_a_comment_and_code_anchors_on_the_code()
    {
        DiffQuote.Locate(Patch, "using System.Text;\n\n// The total of the lines.\n    var total = 0m;").ShouldBe(
            new QuoteMatch(13, null), "the anchor is the first line that is neither blank, nor only a comment, nor a directive");
    }

    [Fact]
    public void A_quote_that_is_missing_not_in_the_patch_found_twice_or_not_code_is_refused_with_its_problem()
    {
        QuoteMatch[] answers =
        [
            DiffQuote.Locate(Patch, null),
            DiffQuote.Locate(Patch, "   "),
            DiffQuote.Locate(Patch, "    total -= 1;"),
            DiffQuote.Locate(Patch, "    return total;"),
            DiffQuote.Locate(Patch, "using System.Text;"),
            DiffQuote.Locate(Patch, "// The total of the lines."),
            DiffQuote.Locate(Patch, "    total += 1;"),
            DiffQuote.Locate(Patch, "    var total = 0m;\n    return total;"),
        ];

        answers.ShouldBe(
            [
                new QuoteMatch(null, DiffQuote.Missing),
                new QuoteMatch(null, DiffQuote.Missing),
                new QuoteMatch(null, DiffQuote.NotFound),
                new QuoteMatch(null, DiffQuote.FoundTimes(2)),
                new QuoteMatch(null, DiffQuote.NotCode),
                new QuoteMatch(null, DiffQuote.NotCode),
                new QuoteMatch(14, null),
                new QuoteMatch(13, null),
            ],
            "no quote, one found twice, and one that is only a directive or a comment each answer their problem; a removed line is placed where the removal happened, and lines that are not next to each other sit on their first line of code, found once alone");
        DiffQuote.FoundTimes(3).ShouldBe("has evidence found 3 times in its file's patch.");
    }

    [Theory]
    [InlineData("using System.Text;", false)]
    [InlineData("global using Scout.Review;", false)]
    [InlineData("using static System.Math;", false)]
    [InlineData("using var stream = File.OpenRead(path);", true)]
    [InlineData("using (var scope = Begin())", true)]
    [InlineData("import { thing } from './thing';", false)]
    [InlineData("from os import path", false)]
    [InlineData("#include <stdio.h>", false)]
    [InlineData("package main", false)]
    [InlineData("# a comment", false)]
    [InlineData("/* a comment */", false)]
    [InlineData(" * a doc line", false)]
    [InlineData("<!-- a comment -->", false)]
    [InlineData("-- a comment", false)]
    [InlineData("\t", false)]
    [InlineData("return total; // keep", true)]
    [InlineData("var important = 1;", true)]
    public void Code_is_any_line_that_is_not_blank_only_a_comment_or_a_directive(string line, bool code) =>
        DiffQuote.IsCode(line).ShouldBe(code, $"the lexical rule reads '{line}' as {(code ? "code" : "not code")}");
}
