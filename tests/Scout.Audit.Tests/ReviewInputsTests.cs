namespace Scout.Audit.Tests;

public sealed class ReviewInputsTests : IDisposable
{
    private readonly TempFolder _folder = new();

    public void Dispose() => _folder.Dispose();

    private void Write(string relative, string text)
    {
        var full = Path.Combine(_folder.Path, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text);
    }

    private static ReviewRemoved NoRemovals => new([], 0, 0, false);

    private static ReviewRules NoRules => new([], [], false);

    [Fact]
    public void A_description_and_an_issue_body_are_cut_at_4000_characters_and_say_so()
    {
        var longer = new ReviewPurpose(
            new string('t', 301), new string('d', 4_001), 42, new string('i', 301), new string('b', 5_000));
        var atTheBound = new ReviewPurpose(new string('t', 300), new string('d', 4_000), 42, "Checkout fails", new string('b', 4_000));
        var splitPair = new ReviewPurpose("Checkout", new string('d', 3_999) + "\uD83D\uDE00" + "d");

        var cut = ReviewInputs.Bound(longer);
        var kept = ReviewInputs.Bound(atTheBound);

        (cut.Title.Length, cut.Description!.Length, cut.IssueTitle!.Length, cut.IssueBody!.Length).ShouldBe(
            (300, 4_000, 300, 4_000), "a title and an issue title are cut at 300 characters, a description and an issue body at 4000");
        (cut.DescriptionCut, cut.IssueBodyCut).ShouldBe((true, true), "a description or an issue body Scout cut says so");
        (cut.Issue, cut.IssueUnread).ShouldBe(((int?)42, false), "bounding keeps the issue's number and its read state");
        kept.ShouldBe(atTheBound, "a purpose within its bounds is kept as it was given, and says nothing was cut");
        ReviewInputs.Bound(splitPair).Description.ShouldBe(new string('d', 3_999), "a cut never splits a surrogate pair");
    }

    [Fact]
    public void The_rule_files_are_read_from_the_snapshot_in_their_fixed_order()
    {
        string[] expected =
        [
            "AGENTS.md", "Claude.md", "CONTRIBUTING.md", ".github/contributing.md", "docs/CONTRIBUTING.md",
            ".claude/rules/a.md", ".claude/rules/b.md", "docs/conventions/y.md", "docs/rules/z.md",
            "context/pages/invariants/a.md", "context/pages/conventions/b.md",
        ];
        foreach (var path in expected.Reverse())
        {
            Write(path, $"Rule kept in {path}.");
        }

        Write("README.md", "Not a rule file.");
        Write(".claude/rules/nested/c.md", "Not directly inside a rule folder.");
        Write("docs/rules/notes.txt", "Not markdown.");

        var rules = ReviewRules.Read(_folder.Path, []);

        rules.Files.Select(f => f.Path).ShouldBe(expected, "the named files come first, whatever their case, then each rule folder's markdown files in ordinal order");
        rules.Files.Select(f => f.Text).ShouldBe(expected.Select(p => $"Rule kept in {p}."), "each rule file's text is read from the snapshot as it is");
        rules.Files.ShouldAllBe(f => !f.Cut && !f.ChangedHere, "a short rule file the pull request does not change is neither cut nor changed here");
        (rules.NotIncluded.Count, rules.Unreadable).ShouldBe((0, false), "every rule file fits and the snapshot was read");
    }

    [Fact]
    public void A_rule_file_is_cut_at_6000_characters_and_the_files_past_16000_or_12_are_named_not_included()
    {
        Write("AGENTS.md", new string('a', 7_000));
        Write("CLAUDE.md", new string('c', 6_000));
        Write("CONTRIBUTING.md", new string('o', 5_000));
        Write(".github/CONTRIBUTING.md", "Holds a NUL\0 byte.");
        Write("docs/CONTRIBUTING.md", new string('m', 1_048_577));
        var small = Enumerable.Range(1, 12).Select(n => $".claude/rules/r{n:00}.md").ToList();
        small.ForEach(path => Write(path, "Keep it.  "));

        var rules = ReviewRules.Read(_folder.Path, []);

        rules.Files.Select(f => (f.Path, f.Text.Length, f.Cut)).ShouldBe(
            [("AGENTS.md", 6_000, true), ("CLAUDE.md", 6_000, false), .. small.Take(10).Select(p => (p, 10, false))],
            "a rule file is cut at 6000 characters and says so, and at most 12 files are given");
        rules.NotIncluded.ShouldBe(
            ["CONTRIBUTING.md", ".github/CONTRIBUTING.md", "docs/CONTRIBUTING.md", .. small.Skip(10)],
            "a file past 16000 characters in total, holding a NUL, over 1 MB or past the twelfth is named not included");
        rules.Files.Sum(f => f.Text.Length).ShouldBeLessThanOrEqualTo(ReviewRules.MaxRulesCharacters, "the rules given never pass 16000 characters in total");
    }

    [Fact]
    public void A_rule_file_the_pull_request_changes_is_marked_changed_here()
    {
        Write("AGENTS.md", "Every write is declared.");
        Write(".claude/rules/orders.md", "An order total is never negative.");

        var rules = ReviewRules.Read(_folder.Path, [".claude/rules/orders.md", "src/Orders/Checkout.cs"]);

        rules.Files.Select(f => (f.Path, f.ChangedHere)).ShouldBe(
            [("AGENTS.md", false), (".claude/rules/orders.md", true)], "a rule file among the pull request's changed paths is marked changed here, and only that one");
    }

    [Fact]
    public void A_snapshot_whose_rule_files_cannot_be_read_gives_rules_marked_unreadable()
    {
        if (OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The unreadable file is made with Unix permissions.");
        }

        Write("AGENTS.md", "Every write is declared.");
        Write(".claude/rules/orders.md", "An order total is never negative.");
        var unreadable = Path.Combine(_folder.Path, ".claude/rules/orders.md");
        File.SetUnixFileMode(unreadable, UnixFileMode.None);
        try
        {
            var rules = ReviewRules.Read(_folder.Path, []);

            rules.ShouldSatisfyAllConditions(
                "a rule file that cannot be read never throws: the rules are marked unreadable and hold no file",
                () => rules.Unreadable.ShouldBeTrue(),
                () => rules.Files.ShouldBeEmpty(),
                () => rules.NotIncluded.ShouldBeEmpty());
        }
        finally
        {
            File.SetUnixFileMode(unreadable, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    [Fact]
    public void A_removed_file_a_removed_test_file_and_an_emptied_test_file_are_each_told_apart()
    {
        ReviewFile[] files =
        [
            new("src/Legacy/Export.cs", null, "removed"),
            new("tests/Shop.Tests/CartTests.cs", "@@ -1,3 +0,0 @@\n-using Xunit;\n-[Fact]\n-public void Adds() {}", "removed"),
            new("tests/Shop.Tests/OrderTests.cs", "@@ -1,3 +0,0 @@\n-using Xunit;\n-[Fact]\n-public void Totals() {}", "modified"),
            new("tests/Shop.Tests/PriceTests.cs", "@@ -1,2 +0,0 @@\n-// header\n-namespace Shop.Tests;\n@@ -9,2 +0,0 @@\n-// end\n-}", "modified"),
            new("src/Shop/Cart.cs", "@@ -1,2 +0,0 @@\n-namespace Shop;\n-class Cart {}", "modified"),
        ];

        var removed = ReviewRemoved.From(files, _folder.Path);

        removed.Entries.Select(e => (e.Path, e.What)).ShouldBe(
        [
            ("tests/Shop.Tests/CartTests.cs", RemovedEntry.TestFileDeleted),
            ("tests/Shop.Tests/OrderTests.cs", RemovedEntry.TestFileEmptied),
            ("src/Legacy/Export.cs", RemovedEntry.FileDeleted),
        ], "a deleted test file, a test file emptied by its one hunk and a deleted file are each told apart, test entries first; a code file emptied is no entry");
        (removed.More, removed.TestFilesWithoutDiff, removed.RepositoryNotSearched).ShouldBe((0, 0, false), "nothing past the bound, every test file had a diff, and the snapshot was searched");
    }

    [Fact]
    public void A_test_file_that_loses_more_test_declarations_than_it_gains_is_counted_from_its_patch_lines()
    {
        ReviewFile[] files =
        [
            new("tests/Shop.Tests/CartTests.cs", "@@ -10,9 +10,4 @@\n-    [Fact]\n-    public void Adds() {}\n-    [Theory]\n+    [Fact]\n+    public void AddsTwo() {}\n     // kept", "modified"),
            new("web/cart.spec.ts", "@@ -1,4 +1,2 @@\n-  it('adds', () => {});\n-  test ('removes', () => {});\n+  it('totals', () => {});", "modified"),
            new("tests/test_cart.py", "@@ -1,2 +1,2 @@\n-def test_adds():\n+def test_totals():", "modified"),
            new("src/Shop/Cart.cs", "@@ -1,2 +1,1 @@\n-    [Fact]\n-    [Fact]", "modified"),
        ];

        var removed = ReviewRemoved.From(files, _folder.Path);

        removed.Entries.Select(e => (e.Path, e.What, e.Removed, e.Added)).ShouldBe(
        [
            ("tests/Shop.Tests/CartTests.cs", RemovedEntry.TestDeclarationsRemoved, 2, 1),
            ("web/cart.spec.ts", RemovedEntry.TestDeclarationsRemoved, 2, 1),
        ], "a test file whose patch removes more test declaration lines than it adds is counted with both counts; as many removed as added, or a code file, is none");
    }

    [Fact]
    public void A_test_file_with_no_diff_is_counted_as_not_examined()
    {
        ReviewFile[] files =
        [
            new("tests/Shop.Tests/CartTests.cs", null, "modified"),
            new("web/cart.spec.ts", null, "modified"),
            new("src/Shop/Cart.cs", null, "modified"),
            new("tests/Shop.Tests/OrderTests.cs", null, "removed"),
        ];

        var removed = ReviewRemoved.From(files, _folder.Path);

        removed.TestFilesWithoutDiff.ShouldBe(2, "each changed test file with no diff is counted as not examined; a code file or a deleted test file is not");
        removed.Entries.ShouldHaveSingleItem().What.ShouldBe(RemovedEntry.TestFileDeleted, "a deleted test file with no diff is still a deleted test file");
    }

    [Fact]
    public void A_deleted_test_file_names_the_code_of_its_stem_still_in_the_snapshot_and_none_when_the_code_went_too()
    {
        _folder.File("src/Shop/Cart.cs");
        _folder.File("web/cart.ts");
        _folder.File("tests/Shop.Tests/Cart.cs");
        _folder.File("docs/Cart.md");
        foreach (var area in new[] { "a", "b", "c", "d" })
        {
            _folder.File($"src/{area}/Checkout.cs");
        }

        ReviewFile[] files =
        [
            new("tests/Shop.Tests/CartTests.cs", null, "removed"),
            new("tests/Shop.Tests/OrderTests.cs", null, "removed"),
            new("tests/Shop.Tests/CheckoutTests.cs", "@@ -1,1 +0,0 @@\n-[Fact]", "modified"),
            new("tests/Shop.Tests/Tests.cs", null, "removed"),
        ];

        var removed = ReviewRemoved.From(files, _folder.Path);

        removed.Entries.Select(e => (e.Path, string.Join(",", e.StillPresent))).ShouldBe(
        [
            ("tests/Shop.Tests/CartTests.cs", "src/Shop/Cart.cs,web/cart.ts"),
            ("tests/Shop.Tests/OrderTests.cs", string.Empty),
            ("tests/Shop.Tests/CheckoutTests.cs", "src/a/Checkout.cs,src/b/Checkout.cs,src/c/Checkout.cs"),
            ("tests/Shop.Tests/Tests.cs", string.Empty),
        ], "a deleted or emptied test file names up to 3 code files of its stem still in the snapshot, whatever their case; none when the code went too or the stem is empty");
        removed.RepositoryNotSearched.ShouldBeFalse("the snapshot was searched");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("no such folder")]
    public void A_snapshot_that_cannot_be_listed_keeps_the_removals_and_says_the_repository_was_not_searched(string? folder)
    {
        var root = folder is null ? null : Path.Combine(_folder.Path, folder);
        ReviewFile[] files = [new("tests/Shop.Tests/CartTests.cs", null, "removed"), new("src/Legacy/Export.cs", null, "removed")];

        var removed = ReviewRemoved.From(files, root);

        removed.Entries.Select(e => (e.Path, e.What, e.StillPresent.Count)).ShouldBe(
            [("tests/Shop.Tests/CartTests.cs", RemovedEntry.TestFileDeleted, 0), ("src/Legacy/Export.cs", RemovedEntry.FileDeleted, 0)],
            "a listing that fails never throws and keeps every removal");
        removed.RepositoryNotSearched.ShouldBeTrue("the removals say the repository was not searched for the code the deleted tests covered");
    }

    [Fact]
    public void A_path_with_a_control_character_is_skipped_and_the_removals_past_200_are_counted_as_more()
    {
        List<ReviewFile> files = [new("src/Legacy/Bad\u0085Name.cs", null, "removed")];
        files.AddRange(Enumerable.Range(1, 205).Select(n => new ReviewFile($"src/Legacy/File{n}.cs", null, "removed")));
        files.Add(new("tests/Shop.Tests/CartTests.cs", null, "removed"));

        var removed = ReviewRemoved.From(files, _folder.Path);

        removed.Entries.Count.ShouldBe(ReviewRemoved.MaxRemovedEntries, "at most 200 removals are kept");
        removed.Entries[0].Path.ShouldBe("tests/Shop.Tests/CartTests.cs", "test entries come first, so the bound never drops one for a deleted file");
        removed.More.ShouldBe(6, "the removals past 200 are counted, not named");
        removed.Entries.ShouldNotContain(e => e.Path.Any(char.IsControl), "a path with a control character never becomes an entry");
    }

    [Fact]
    public void A_batch_is_handed_the_removals_of_its_own_files_and_the_test_files_named_like_them()
    {
        var removed = new ReviewRemoved(
        [
            new("tests/Shop.Tests/CartTests.cs", RemovedEntry.TestFileDeleted, ["src/Shop/Cart.cs"], 0, 0),
            new("tests/Shop.Tests/OrderTests.cs", RemovedEntry.TestDeclarationsRemoved, [], 3, 1),
            new("src/Legacy/Export.cs", RemovedEntry.FileDeleted, [], 0, 0),
        ], 0, 0, false);

        removed.For(["src/Shop/Cart.cs"]).Select(e => e.Path).ShouldBe(["tests/Shop.Tests/CartTests.cs"], "a batch with the code a deleted test covered is handed that test's removal");
        removed.For(["src/Shop/order.cs"]).Select(e => e.Path).ShouldBe(["tests/Shop.Tests/OrderTests.cs"], "a batch with a file of the test's stem, whatever its case, is handed it");
        removed.For(["src/Legacy/Export.cs", "tests/Shop.Tests/CartTests.cs"]).Select(e => e.Path).ShouldBe(
            ["tests/Shop.Tests/CartTests.cs", "src/Legacy/Export.cs"], "a batch is handed the removals of its own files");
        removed.For(["src/Shop/Export.cs"]).ShouldBeEmpty("a deleted file that is not a test is handed only to its own batch");
    }

    [Fact]
    public void The_notes_count_what_was_removed_and_each_input_that_could_not_be_read_and_never_name_a_path()
    {
        var inputs = new ReviewInputs(
            new ReviewPurpose("Checkout", null, 42, IssueUnread: true),
            new ReviewRules([], [], Unreadable: true),
            new ReviewRemoved(
            [
                new("tests/Shop.Tests/CartTests.cs", RemovedEntry.TestFileDeleted, ["src/Shop/Cart.cs"], 0, 0),
                new("tests/Shop.Tests/PriceTests.cs", RemovedEntry.TestFileDeleted, [], 0, 0),
                new("tests/Shop.Tests/OrderTests.cs", RemovedEntry.TestFileEmptied, ["src/Shop/Order.cs"], 0, 0),
                new("web/cart.spec.ts", RemovedEntry.TestDeclarationsRemoved, [], 2, 1),
                new("src/Legacy/Export.cs", RemovedEntry.FileDeleted, [], 0, 0),
            ], 0, 4, RepositoryNotSearched: true));

        var notes = inputs.Notes();

        notes.ShouldBe(
        [
            "The pull request deletes 3 files, 2 of them test files.",
            "2 deleted or emptied test files leave code named like them in the repository.",
            "1 test file is emptied.",
            "1 test file loses more test declarations than it gains.",
            "4 changed test files had no diff to count removed tests in.",
            "Scout could not look in the repository for the code the deleted tests covered.",
            "The linked issue could not be read.",
            "The project's rule files could not be read.",
        ], "each note is Scout's sentence with its count or flag");
        notes.ShouldNotContain(n => n.Contains('/', StringComparison.Ordinal) || n.Contains("Checkout", StringComparison.Ordinal), "a note never names a path or a word of the pull request");
        new ReviewInputs(new ReviewPurpose("Checkout", null), NoRules, NoRemovals).Notes().ShouldBeEmpty("a note appears only when its count is above zero or its flag is set");
        new ReviewInputs(null, NoRules, NoRemovals).Notes().ShouldBeEmpty("no purpose given is no note of an unread issue");
    }

    [Fact]
    public void Each_note_agrees_with_its_number()
    {
        var one = new ReviewInputs(null, NoRules, new ReviewRemoved(
        [
            new("tests/Shop.Tests/CartTests.cs", RemovedEntry.TestFileDeleted, ["src/Shop/Cart.cs"], 0, 0),
            new("tests/Shop.Tests/OrderTests.cs", RemovedEntry.TestFileEmptied, [], 0, 0),
            new("web/cart.spec.ts", RemovedEntry.TestDeclarationsRemoved, [], 2, 1),
        ], 0, 1, false));
        var codeOnly = new ReviewInputs(null, NoRules, new ReviewRemoved([new("src/Legacy/Export.cs", RemovedEntry.FileDeleted, [], 0, 0)], 0, 0, false));
        var oneTestOfThree = new ReviewInputs(null, NoRules, new ReviewRemoved(
        [
            new("tests/Shop.Tests/CartTests.cs", RemovedEntry.TestFileDeleted, [], 0, 0),
            new("src/Legacy/Export.cs", RemovedEntry.FileDeleted, [], 0, 0),
            new("src/Legacy/Import.cs", RemovedEntry.FileDeleted, [], 0, 0),
        ], 0, 0, false));

        one.Notes().ShouldBe(
        [
            "The pull request deletes 1 file, a test file.",
            "1 deleted or emptied test file leaves code named like it in the repository.",
            "1 test file is emptied.",
            "1 test file loses more test declarations than it gains.",
            "1 changed test file had no diff to count removed tests in.",
        ], "a count of one is said in the singular in every note");
        codeOnly.Notes().ShouldBe(["The pull request deletes 1 file, not a test file."], "one deleted file that is not a test says so in the singular");
        oneTestOfThree.Notes().ShouldBe(["The pull request deletes 3 files, 1 of them a test file."], "one test file among several deleted is said in the singular");
    }

    [Fact]
    public void The_notes_count_every_removal_those_past_the_bound_among_them()
    {
        List<ReviewFile> files = [.. Enumerable.Range(1, 205).Select(n => new ReviewFile($"src/Legacy/File{n}.cs", null, "removed"))];
        files.Add(new("tests/Shop.Tests/CartTests.cs", null, "removed"));

        var removed = ReviewRemoved.From(files, _folder.Path);
        var notes = new ReviewInputs(null, NoRules, removed).Notes();

        removed.More.ShouldBe(6, "the removals past 200 are not kept as entries");
        notes.ShouldBe(["The pull request deletes 206 files, 1 of them a test file."], "the deletions are counted over every removal, those past the bound among them");
    }

    [Fact]
    public void Past_20_rule_files_not_included_the_rest_are_counted_not_named()
    {
        var paths = Enumerable.Range(1, 300).Select(n => $".claude/rules/r{n:000}.md").ToList();
        paths.ForEach(path => Write(path, "Keep it."));

        var rules = ReviewRules.Read(_folder.Path, []);

        rules.Files.Count.ShouldBe(ReviewRules.MaxRuleFiles, "at most 12 rule files are given");
        rules.NotIncluded.ShouldBe(paths.Skip(12).Take(20), "at most 20 rule files that did not fit are named, in their order");
        rules.NotIncludedMore.ShouldBe(268, "the rule files past the 20 named are counted");
    }

    [Fact]
    public void A_rule_file_past_the_bound_is_named_without_being_read()
    {
        if (OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The unreadable file is made with Unix permissions.");
        }

        var kept = Enumerable.Range(1, 12).Select(n => $".claude/rules/r{n:00}.md").ToList();
        kept.ForEach(path => Write(path, "Keep it."));
        Write(".claude/rules/r13.md", "Past the bound.");
        var unreadable = Path.Combine(_folder.Path, ".claude/rules/r13.md");
        File.SetUnixFileMode(unreadable, UnixFileMode.None);
        try
        {
            var rules = ReviewRules.Read(_folder.Path, []);

            rules.Unreadable.ShouldBeFalse("a file past the bound is not read, so a file that cannot be read there does not make the rules unreadable");
            rules.Files.Select(f => f.Path).ShouldBe(kept, "the twelve files that fit are kept");
            rules.NotIncluded.ShouldBe([".claude/rules/r13.md"], "the file past the bound is named as not included");
        }
        finally
        {
            File.SetUnixFileMode(unreadable, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }
}
