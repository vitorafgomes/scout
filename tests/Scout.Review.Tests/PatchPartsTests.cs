namespace Scout.Review.Tests;

/// <summary>A file's patch read in parts: every part a valid patch of the file, within the bound, with the whole patch's line numbers.</summary>
public sealed class PatchPartsTests
{
    private static string Hunk(int oldStart, int newStart, int context, int removed, int added, string marker = "x") =>
        $"@@ -{oldStart},{context + removed} +{newStart},{context + added} @@ fn()\n"
        + string.Join('\n', Enumerable.Range(0, context).Select(i => $" ctx {marker}{i}")
            .Concat(Enumerable.Range(0, removed).Select(i => $"-old {marker}{i}"))
            .Concat(Enumerable.Range(0, added).Select(i => $"+new {marker}{i} {new string('a', 20)}")));

    public static TheoryData<string, string> Shapes => new()
    {
        { "many hunks", string.Join('\n', Enumerable.Range(0, 30).Select(h => Hunk(1 + (h * 50), 1 + (h * 60), 3, 2, 12, $"h{h}"))) },
        { "one huge hunk", Hunk(10, 10, 40, 30, 400) },
        { "additions only", Hunk(0, 1, 0, 0, 500) },
        { "deletions only", Hunk(1, 0, 0, 500, 0) },
        { "crlf", string.Join('\n', Enumerable.Range(0, 20).Select(h => Hunk(1 + (h * 40), 1 + (h * 40), 2, 1, 20, $"c{h}"))).Replace("\n", "\r\n", StringComparison.Ordinal) },
        { "no newline at the end", Hunk(1, 1, 2, 1, 200) + "\n\\ No newline at end of file" },
    };

    [Theory]
    [MemberData(nameof(Shapes))]
    public void P1_P2_every_changed_line_is_in_exactly_one_part_with_the_whole_patch_s_number_and_each_part_fits(string shape, string patch)
    {
        const int Bound = 2_000;

        var parts = PatchParts.Split(patch, Bound);

        parts.Count.ShouldBeGreaterThan(1, $"{shape}: a patch over the bound is read in parts");
        parts.ShouldAllBe(p => p.Problem == null && p.Patch != null && p.Patch.Length <= Bound, $"{shape}: each part fits the bound");
        var lines = parts.SelectMany(p => DiffLines.RightSide(p.Patch)).ToList();
        lines.ShouldBe(DiffLines.RightSide(patch), $"{shape}: the parts' new-file lines, with their numbers, are the whole patch's, each once");
        var removed = parts.Sum(p => p.Patch!.Split('\n').Count(l => l.StartsWith('-')));
        removed.ShouldBe(patch.Split('\n').Count(l => l.StartsWith('-')), $"{shape}: every removed line is in one part");
        parts.Where(p => p.FirstLine > 0).ShouldAllBe(p => p.FirstLine <= p.LastLine, $"{shape}: each part says the new-file lines it covers");
    }

    [Fact]
    public void P2_a_quote_in_a_part_is_located_on_the_whole_patch_s_line()
    {
        var patch = Hunk(10, 10, 40, 30, 400);
        var quote = "+new x350 " + new string('a', 20);
        var whole = DiffQuote.Locate(patch, quote[1..]).Line;

        var inPart = PatchParts.Split(patch, 2_000).Select(p => DiffQuote.Locate(p.Patch, quote[1..]).Line).Where(l => l is not null).ToList();

        inPart.ShouldBe([whole], "the quote is found in one part, on the line the whole patch gives it");
    }

    [Fact]
    public void P5_a_diff_line_longer_than_the_bound_is_said_and_never_dropped()
    {
        var patch = "@@ -1,3 +1,3 @@\n a\n-b\n+" + new string('z', 5_000) + "\n" + string.Join('\n', Enumerable.Range(0, 200).Select(i => $"+line {i} {new string('q', 20)}"));

        var parts = PatchParts.Split(patch, 2_000);

        var impossible = parts.Where(p => p.Problem != null).ToList();
        impossible.ShouldHaveSingleItem().ShouldSatisfyAllConditions(
            "the line that cannot fit is a part of its own that says why",
            p => p.Patch.ShouldBeNull(),
            p => p.FirstLine.ShouldBe(2),
            p => p.Problem.ShouldBe("line 2 of the diff is 5,001 characters, more than a batch holds"));
        parts.Where(p => p.Patch != null).SelectMany(p => DiffLines.RightSide(p.Patch)).Count()
            .ShouldBe(DiffLines.RightSide(patch).Count - 1, "every other line is still in a part");
    }

    [Fact]
    public void A_patch_within_the_bound_is_one_part_and_the_same_patch_always_splits_the_same_way()
    {
        PatchParts.Split("@@ -1 +1 @@\n-a\n+b", 2_000).ShouldBe([new PatchPart("@@ -1 +1 @@\n-a\n+b", 1, 1)]);
        var patch = Hunk(10, 10, 40, 30, 400);
        PatchParts.Split(patch, 2_000).ShouldBe(PatchParts.Split(patch, 2_000), "a restart finds the same parts again");
    }
}
