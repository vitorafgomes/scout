namespace Scout.Plugin.GitHub.Tests;

public sealed class DiffLinesTests
{
    private const string Patch = """
        @@ -10,4 +10,5 @@ class Checkout
         var total = 0m;
        -foreach (var line in lines) total += line.Price;
        +foreach (var line in lines)
        +{
        +    total += line.Price * line.Quantity;
        +}
         return total;
        @@ -40,2 +41,2 @@
        -old
        +new
         kept
        """;

    [Fact]
    public void Added_And_Context_Lines_Are_Anchors_On_The_Right_Side_And_Removed_Lines_Are_Not()
    {
        var lines = DiffLines.RightSideLines(Patch);

        lines.ShouldBe(new HashSet<int> { 10, 11, 12, 13, 14, 15, 41, 42 });
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not a diff at all")]
    public void No_Patch_Means_No_Anchor(string? patch) =>
        DiffLines.RightSideLines(patch).ShouldBeEmpty();

    [Fact]
    public void A_Hunk_Without_A_Count_Starts_At_Its_Line() =>
        DiffLines.RightSideLines("@@ -1 +1 @@\n-a\n+b").ShouldBe(new HashSet<int> { 1 });

    [Fact]
    public void A_Trailing_Newline_Or_An_Empty_Line_Adds_No_Phantom_Anchor() =>
        DiffLines.RightSideLines("@@ -1 +1 @@\n-a\n+b\n").ShouldBe(new HashSet<int> { 1 });

    [Fact]
    public void A_No_Newline_Marker_Does_Not_Count_As_A_Line() =>
        DiffLines.RightSideLines("@@ -1,2 +1,2 @@\n a\n+b\n\\ No newline at end of file").ShouldBe(new HashSet<int> { 1, 2 });
}
