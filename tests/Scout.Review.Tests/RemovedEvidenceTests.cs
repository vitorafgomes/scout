namespace Scout.Review.Tests;

/// <summary>A finding about removed lines quotes them, and Scout places it where the removal happened.</summary>
public sealed class RemovedEvidenceTests
{
    private const string Hunks =
        "@@ -10,6 +10,4 @@ class Peek\n"
        + " var queue = Find(name);\n"
        + "-[Fact]\n"
        + "-public void Peek_fails_when_the_queue_is_gone() { }\n"
        + " return queue.Peek();\n"
        + " }\n"
        + "-// trailing note\n"
        + "-Assert.Throws(() => Peek());\n"
        + "@@ -40,2 +38,0 @@\n"
        + "-[Fact]\n"
        + "-public void Resubmit_fails_when_refused() { }";

    [Theory]
    [InlineData("public void Peek_fails_when_the_queue_is_gone() { }", 11, "the first new-side line after the removed run")]
    [InlineData("[Fact]\npublic void Peek_fails_when_the_queue_is_gone() { }", 11, "a quote of adjacent removed lines")]
    [InlineData("Assert.Throws(() => Peek());", 12, "the last new-side line before a run with nothing after it")]
    [InlineData("public void Resubmit_fails_when_refused() { }", 38, "the hunk's new start in a hunk with no new-side line")]
    public void E1_a_finding_that_quotes_removed_lines_is_placed_where_the_removal_happened(string quote, int line, string rule) =>
        DiffQuote.Locate(Hunks, quote).ShouldBe(new QuoteMatch(line, null), rule);

    [Fact]
    public void E1_in_a_part_the_whole_file_s_numbers_are_kept()
    {
        var patch = string.Join('\n', Enumerable.Range(0, 40).Select(h =>
            $"@@ -{1 + (h * 30)},3 +{1 + (h * 30)},2 @@\n ctx {h}\n-removed {h} {new string('r', 40)}\n+added {h} {new string('a', 40)}"));
        var whole = DiffQuote.Locate(patch, $"removed 33 {new string('r', 40)}");

        var inParts = PatchParts.Split(patch, 1_000).Select(p => DiffQuote.Locate(p.Patch, $"removed 33 {new string('r', 40)}")).Where(m => m.Line is not null).ToList();

        whole.ShouldBe(new QuoteMatch((33 * 30) + 2, null), "placed on the added line that follows the removal");
        inParts.ShouldBe([whole], "a part gives the same line as the whole patch");
    }

    [Fact]
    public void E1_the_derived_line_is_a_line_of_the_diff_a_note_can_comment_on_unless_its_hunk_only_removes()
    {
        var onTheDiff = DiffLines.RightSideLines(Hunks);

        onTheDiff.ShouldContain(11, "a line after the removal is a new-side line of the hunk");
        onTheDiff.ShouldContain(12, "a line before the removal is a new-side line of the hunk");
        onTheDiff.ShouldNotContain(38, "a hunk with no new-side line gives its new start, which no inline comment can sit on");
    }

    [Fact]
    public void E2_a_quote_on_the_new_side_is_placed_as_today_one_on_both_sides_on_the_new_side_and_one_on_neither_is_not_reported()
    {
        DiffQuote.Locate(Hunks, "return queue.Peek();").ShouldBe(new QuoteMatch(11, null), "the new side, as today");
        DiffQuote.Locate("@@ -1,3 +1,3 @@\n-x = 1;\n y\n+x = 1;", "x = 1;").ShouldBe(new QuoteMatch(2, null), "a quote on both sides is placed on the new side");
        DiffQuote.Locate(Hunks, "nowhere();").ShouldBe(new QuoteMatch(null, DiffQuote.NotFound), "a quote on neither side is not reported");
        DiffQuote.Locate(Hunks, "[Fact]").ShouldBe(new QuoteMatch(null, DiffQuote.FoundTimes(2)), "a removed quote found twice is not placed");
        DiffQuote.Locate(Hunks, "// trailing note").ShouldBe(new QuoteMatch(null, DiffQuote.NotCode), "a removed comment alone is not evidence");
    }

    [Fact]
    public void E3_the_hunk_handed_to_the_second_reading_holds_the_removed_lines_a_finding_quotes()
    {
        var first = DiffQuote.Locate(Hunks, "public void Peek_fails_when_the_queue_is_gone() { }").Line!.Value;
        var last = DiffQuote.Locate(Hunks, "public void Resubmit_fails_when_refused() { }").Line!.Value;

        DiffLines.HunkHolding(Hunks, first).ShouldNotBeNull().ShouldContain("-public void Peek_fails_when_the_queue_is_gone() { }", Case.Sensitive, "the claim's removed lines are handed with the hunk");
        DiffLines.HunkHolding(Hunks, last).ShouldBe("@@ -40,2 +38,0 @@\n-[Fact]\n-public void Resubmit_fails_when_refused() { }", "a hunk that only removes is found by its new start");
    }

    [Fact]
    public void E3_a_long_hunk_handed_to_the_second_reading_keeps_the_removed_lines_the_finding_quotes()
    {
        var removedRun = string.Concat(Enumerable.Range(1, 200).Select(i => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"-    Assert.Equal({i}, Peek());\n")));
        var patch = "@@ -1,202 +1,2 @@\n class PeekTests\n" + removedRun + " }";
        var quote = "Assert.Equal(7, Peek());";
        var line = DiffQuote.Locate(patch, quote).Line!.Value;

        var hunk = DiffLines.HunkHolding(patch, line, quote).ShouldNotBeNull();

        line.ShouldBe(2, "the removal sits on the line after the removed run");
        hunk.ShouldContain("-    Assert.Equal(7, Peek());", Case.Sensitive, "the window keeps the removed line the finding quotes, not only the lines before the line it sits on");
        DiffLines.HunkHolding(patch, line).ShouldNotBeNull().ShouldNotContain("-    Assert.Equal(7, Peek());", Case.Sensitive, "without the quote the window sits around the line, as before");
    }
}
