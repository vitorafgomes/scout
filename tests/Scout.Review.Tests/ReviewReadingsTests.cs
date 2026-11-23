namespace Scout.Review.Tests;

/// <summary>The focused readings of a review batch, and when each applies.</summary>
public sealed class ReviewReadingsTests
{
    private const string Adds = "@@ -1,2 +1,3 @@\n a\n+b\n c";
    private const string Removes = "@@ -1,3 +1,2 @@\n a\n-[Fact] public void Refuses() { }\n c";
    private static (string Path, string? Patch)[] Code(params string?[] patches) => [.. patches.Select((p, i) => ($"src/File{i}.cs", p))];

    private const string Asked = "## What was asked\n\n- R1: Purging asks for a count.\n- R2: A refused read is mapped to 403.\n";

    [Fact]
    public void F1_a_batch_whose_patches_remove_lines_is_read_for_what_was_removed_and_one_that_removes_nothing_is_not()
    {
        ReviewReadings.For(Code(Adds, Removes), null).Select(r => r.Name).ShouldBe([ReviewReadings.Removed], "a removal gets the removed reading");
        ReviewReadings.For(Code(Adds, null), null).ShouldBeEmpty("a batch that removes nothing has no removed reading");
        ReviewReadings.RemovesLines("--- a/x\n+++ b/x\n@@ -0,0 +1 @@\n+a").ShouldBeFalse("a file header is not a removed line");
    }

    [Fact]
    public void F2_a_review_whose_description_carries_requests_is_read_request_by_request_and_one_without_is_not()
    {
        ReviewReadings.For(Code(Adds), Asked).Select(r => r.Name).ShouldBe([ReviewReadings.Requests], "requests get the requests reading");
        ReviewReadings.For(Code(Adds), "Just a refactor.").ShouldBeEmpty("a description that lists no request gets no requests reading");
        ReviewReadings.For(Code(Removes), Asked).Select(r => r.Name).ShouldBe([ReviewReadings.Removed, ReviewReadings.Requests], "both, removed first");
    }

    [Fact]
    public void Each_reading_says_its_task_in_its_own_paragraph()
    {
        ReviewReadings.For(Code(Removes), Asked).Select(r => r.Task).ShouldBe([ReviewReadings.RemovedTask, ReviewReadings.RequestsTask]);
        ReviewReadings.RemovedTask.ShouldStartWith("This reading looks only at what the patches remove", Case.Sensitive);
        ReviewReadings.RequestsTask.ShouldContain("sibling operations of the one that changed included", Case.Sensitive);
    }

    [Fact]
    public void C1_a_batch_with_no_code_or_test_file_gets_no_focused_reading_and_one_with_a_code_file_gets_them()
    {
        (string, string?)[] pages = [("context/pages/index.md", Removes), ("ui/prototype/Main.dc.html", Removes), ("appsettings.json", Removes)];
        (string, string?)[] withCode = [.. pages, ("src/Orders/Gateway.cs", Removes)];

        ReviewReadings.For(pages, Asked).ShouldBeEmpty("documentation, prototypes and configuration get neither the removed nor the requests reading");
        ReviewReadings.For(withCode, Asked).Select(r => r.Name).ShouldBe([ReviewReadings.Removed, ReviewReadings.Requests], "a batch with a code file gets both");
        ReviewReadings.For([("tests/Orders.Tests/GatewayTests.cs", Adds)], Asked).Select(r => r.Name).ShouldBe([ReviewReadings.Requests], "a test file counts as code for the requests reading");
    }

    [Fact]
    public void A_removed_line_whose_own_text_starts_with_two_dashes_is_a_removal()
    {
        const string RemovedComment = "@@ -1,3 +1,2 @@\n select 1;\n--- the total of the orders\n select 2;";

        ReviewReadings.RemovesLines(RemovedComment).ShouldBeTrue("a removed line starting with -- is removed, as the diff counts it");
        ReviewReadings.RemovesLines("@@ -1,2 +1,3 @@\n a\n+b\n c").ShouldBeFalse("a patch that only adds removes nothing");
    }

    [Theory]
    [InlineData("src/Data/Context/AppDbContext.cs", 0)]
    [InlineData("website/docs/build.js", 0)]
    [InlineData("context/pages/index.md", 3)]
    [InlineData("docs/api/openapi.json", 3)]
    public void A_documentation_folder_counts_at_the_repository_s_root_only(string path, int order) =>
        ReviewReadings.ReadingOrder(path).ShouldBe(order, $"{path}: a folder named docs or context deeper in the tree holds code like any other");
}
