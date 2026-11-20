using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.RegularExpressions;
using Scout.Knowledge;

namespace Scout.Audit;

/// <summary>
/// What a pull request says it does: its title and description, the issue it names with that issue's title and body,
/// whether the issue could not be read, and whether Scout cut the description or the issue's body.
/// </summary>
public sealed record ReviewPurpose(
    string Title, string? Description, int? Issue = null, string? IssueTitle = null, string? IssueBody = null, bool IssueUnread = false,
    bool DescriptionCut = false, bool IssueBodyCut = false);

/// <summary>One of the project's rule files at the reviewed commit: its path, its text, whether the text was cut, and whether the pull request changes it.</summary>
internal sealed record ReviewRuleFile(string Path, string Text, bool Cut, bool ChangedHere);

/// <summary>
/// The project's own rule files, read by Scout from the snapshot in a fixed order: those given, the paths of those that did
/// not fit, whether the files could not be read at all, and how many more did not fit than the paths named.
/// </summary>
internal sealed record ReviewRules(IReadOnlyList<ReviewRuleFile> Files, IReadOnlyList<string> NotIncluded, bool Unreadable, int NotIncludedMore = 0)
{
    internal const int MaxRuleFiles = 12;
    internal const int MaxNotIncluded = 20;
    internal const int MaxRuleFileCharacters = 6_000;
    internal const int MaxRulesCharacters = 16_000;
    private const long MaxRuleFileBytes = 1_048_576;

    private static readonly string[] NamedFiles = ["AGENTS.md", "CLAUDE.md", "CONTRIBUTING.md", ".github/CONTRIBUTING.md", "docs/CONTRIBUTING.md"];

    private static readonly string[] RuleFolders = [".claude/rules", "docs/conventions", "docs/rules", "context/pages/invariants", "context/pages/conventions"];

    /// <summary>
    /// Reads the rule files under <paramref name="root"/>: the named files, whatever their case, then the <c>*.md</c> files
    /// directly inside the rule folders in ordinal order. A file is cut at <see cref="MaxRuleFileCharacters"/>; one past
    /// <see cref="MaxRuleFiles"/> or <see cref="MaxRulesCharacters"/> in total, over 1 MB or holding a NUL is named not
    /// included, at most <see cref="MaxNotIncluded"/> of them by path and the rest counted; once the files or the total are
    /// full, a file is not read at all. A file among
    /// <paramref name="changedPaths"/> is marked changed here. A failed read never throws: it answers
    /// no file, marked unreadable.
    /// </summary>
    internal static ReviewRules Read(string root, IReadOnlyCollection<string> changedPaths)
    {
        try
        {
            List<ReviewRuleFile> files = [];
            List<string> notIncluded = [];
            var more = 0;
            var total = 0;
            foreach (var path in Candidates(root))
            {
                var relative = ProjectFileFilter.ToRelativePath(root, path);
                var full = files.Count == MaxRuleFiles || total >= MaxRulesCharacters;
                var text = full || new FileInfo(path).Length > MaxRuleFileBytes ? null : File.ReadAllText(path);
                var kept = ReviewInputs.Cut(text, MaxRuleFileCharacters);
                if (text is null || kept is null || text.Contains('\0', StringComparison.Ordinal) || files.Count == MaxRuleFiles || total + kept.Length > MaxRulesCharacters)
                {
                    if (notIncluded.Count < MaxNotIncluded)
                    {
                        notIncluded.Add(relative);
                    }
                    else
                    {
                        more++;
                    }

                    continue;
                }

                total += kept.Length;
                files.Add(new(relative, kept, kept.Length < text.Length, changedPaths.Contains(relative, StringComparer.Ordinal)));
            }

            return new(files, notIncluded, Unreadable: false, more);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new([], [], Unreadable: true);
        }
    }

    private static IEnumerable<string> Candidates(string root)
    {
        var anyCase = new EnumerationOptions { MatchCasing = MatchCasing.CaseInsensitive, IgnoreInaccessible = false };
        var exact = new EnumerationOptions { IgnoreInaccessible = false };
        foreach (var named in NamedFiles)
        {
            var folder = Path.Combine(root, Path.GetDirectoryName(named)!);
            if (Directory.Exists(folder))
            {
                foreach (var found in Directory.EnumerateFiles(folder, Path.GetFileName(named), anyCase).Order(StringComparer.Ordinal))
                {
                    yield return found;
                }
            }
        }

        foreach (var folder in RuleFolders.Select(f => Path.Combine(root, f)).Where(Directory.Exists))
        {
            foreach (var found in Directory.EnumerateFiles(folder, "*.md", exact).Order(StringComparer.Ordinal))
            {
                yield return found;
            }
        }
    }
}

/// <summary>
/// One thing a pull request removed: the file's path, what was removed, one of the constants of this record, the code
/// named like a removed test file still in the repository, and, for test declarations removed, how many patch lines
/// removed and added one.
/// </summary>
internal sealed record RemovedEntry(string Path, string What, IReadOnlyList<string> StillPresent, int Removed, int Added)
{
    internal const string FileDeleted = "file deleted";
    internal const string TestFileDeleted = "test file deleted";
    internal const string TestFileEmptied = "test file emptied";
    internal const string TestDeclarationsRemoved = "test declarations removed";
}

/// <summary>How many of each removal a pull request holds, counted over every entry, those past the bound among them.</summary>
internal sealed record RemovedCounts(int FilesDeleted, int TestFilesDeleted, int LeftBehind, int TestFilesEmptied, int TestDeclarationsRemoved)
{
    /// <summary>The counts of <paramref name="entries"/>.</summary>
    internal static RemovedCounts Of(IReadOnlyCollection<RemovedEntry> entries) => new(
        entries.Count(e => e.What is RemovedEntry.FileDeleted or RemovedEntry.TestFileDeleted),
        entries.Count(e => e.What is RemovedEntry.TestFileDeleted),
        entries.Count(e => e.What is RemovedEntry.TestFileDeleted or RemovedEntry.TestFileEmptied && e.StillPresent.Count > 0),
        entries.Count(e => e.What is RemovedEntry.TestFileEmptied),
        entries.Count(e => e.What is RemovedEntry.TestDeclarationsRemoved));
}

/// <summary>
/// What a pull request removed, computed by Scout from the changed files: the entries, test entries first, how many more
/// did not fit, how many changed test files had no diff to count in, whether the repository could not be searched for
/// the code a deleted test covered, and the counts over every entry, those not kept among them.
/// </summary>
internal sealed partial record ReviewRemoved(
    IReadOnlyList<RemovedEntry> Entries, int More, int TestFilesWithoutDiff, bool RepositoryNotSearched, RemovedCounts? Counts = null)
{
    internal const int MaxRemovedEntries = 200;

    private const int MaxStillPresent = 3;

    private static readonly string[] StemSuffixes = ["Tests", "Test", "Spec", "_test", ".test", ".spec"];

    // ponytail: a line pattern, not a parse, so a declaration split over lines or a framework it does not name is missed; parse the test file to count them exactly.
    [GeneratedRegex(@"^\s*(\[(Fact|Theory|Test|TestMethod|TestCase)\b|@(Test|ParameterizedTest)\b|(it|test)\s*\(|def test_|func Test|fn test_|#\[test\])")]
    private static partial Regex TestDeclaration();

    [GeneratedRegex(@"^@@ -\d+(,\d+)? \+0,0 @@")]
    private static partial Regex EmptiedHunk();

    /// <summary>
    /// What <paramref name="files"/> removed: a removed file, a removed or emptied test file, and a test file whose patch
    /// removes more test declarations than it adds; a path with a control character is skipped. For a deleted or emptied
    /// test file, the code at <paramref name="root"/> named like it. A null root or a failed listing never throws: it marks
    /// the repository not searched.
    /// </summary>
    internal static ReviewRemoved From(IReadOnlyList<ReviewFile> files, string? root)
    {
        List<RemovedEntry> tests = [];
        List<RemovedEntry> deleted = [];
        var withoutDiff = 0;
        foreach (var file in files.Where(f => !f.Path.Any(char.IsControl)))
        {
            var test = RepositoryAudits.ReadingOrder(file.Path) == 1;
            if (file.Status == "removed")
            {
                (test ? tests : deleted).Add(new(file.Path, test ? RemovedEntry.TestFileDeleted : RemovedEntry.FileDeleted, [], 0, 0));
            }
            else if (test && file.Patch is null)
            {
                withoutDiff++;
            }
            else if (test && IsEmptied(file.Patch!))
            {
                tests.Add(new(file.Path, RemovedEntry.TestFileEmptied, [], 0, 0));
            }
            else if (test && Declarations(file.Patch!) is var (removed, added) && removed > added)
            {
                tests.Add(new(file.Path, RemovedEntry.TestDeclarationsRemoved, [], removed, added));
            }
        }

        var notSearched = false;
        if (tests.Any(e => e.What is RemovedEntry.TestFileDeleted or RemovedEntry.TestFileEmptied))
        {
            var code = Code(root);
            notSearched = code is null;
            tests = [.. tests.Select(e => code is null || e.What is RemovedEntry.TestDeclarationsRemoved || Stem(e.Path) is not { Length: > 0 } stem
                ? e
                : e with { StillPresent = [.. code[stem].Order(StringComparer.Ordinal).Take(MaxStillPresent)] })];
        }

        List<RemovedEntry> all = [.. tests, .. deleted];
        return new([.. all.Take(MaxRemovedEntries)], Math.Max(0, all.Count - MaxRemovedEntries), withoutDiff, notSearched, RemovedCounts.Of(all));
    }

    /// <summary>The entries a batch of <paramref name="batchFiles"/> is handed: those of its own files, and the test entries whose stem names one of its files.</summary>
    internal IReadOnlyList<RemovedEntry> For(IReadOnlyList<string> batchFiles)
    {
        var names = batchFiles.Select(Path.GetFileNameWithoutExtension).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return [.. Entries.Where(e => batchFiles.Contains(e.Path, StringComparer.Ordinal)
            || (e.What != RemovedEntry.FileDeleted && Stem(e.Path) is { Length: > 0 } stem && names.Contains(stem)))];
    }

    /// <summary>
    /// A test file's name without its extension and without one affix of <c>Tests</c>, <c>Test</c>, <c>Spec</c>,
    /// <c>_test</c>, <c>.test</c>, <c>.spec</c> or <c>test_</c>.
    /// </summary>
    internal static string Stem(string path)
    {
        // ponytail: a name match, not a call graph, so a test named unlike its code finds nothing and a namesake elsewhere counts; read who calls the changed code to find it exactly.
        var name = Path.GetFileNameWithoutExtension(path);
        var suffix = StemSuffixes.FirstOrDefault(s => name.EndsWith(s, StringComparison.Ordinal));
        return suffix is not null ? name[..^suffix.Length] : name.StartsWith("test_", StringComparison.Ordinal) ? name["test_".Length..] : name;
    }

    private static ILookup<string, string>? Code(string? root)
    {
        if (root is null)
        {
            return null;
        }

        try
        {
            return Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                .Select(path => ProjectFileFilter.ToRelativePath(root, path))
                .Where(path => ProjectFileFilter.IsIndexable(path) && RepositoryAudits.ReadingOrder(path) == 0)
                .ToLookup(path => Path.GetFileNameWithoutExtension(path), StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool IsEmptied(string patch)
    {
        var hunks = patch.Split('\n').Where(line => line.StartsWith("@@", StringComparison.Ordinal)).ToList();
        return hunks.Count == 1 && EmptiedHunk().IsMatch(hunks[0]);
    }

    private static (int Removed, int Added) Declarations(string patch)
    {
        var lines = patch.Split('\n');
        return (Count(lines, '-'), Count(lines, '+'));

        static int Count(string[] lines, char sign) =>
            lines.Count(line => line.Length > 0 && line[0] == sign && !line.StartsWith(new string(sign, 3), StringComparison.Ordinal) && TestDeclaration().IsMatch(line[1..]));
    }
}

/// <summary>
/// The three inputs a review batch is handed, computed by Scout when the review starts and kept with the job: what the pull
/// request says it does, the project's own rule files, and what the pull request removed.
/// </summary>
internal sealed record ReviewInputs(ReviewPurpose? Purpose, ReviewRules Rules, ReviewRemoved Removed)
{
    internal const int MaxTitle = 300;
    internal const int MaxBody = 4_000;

    /// <summary>The purpose with each title cut at <see cref="MaxTitle"/> and the description and the issue's body at <see cref="MaxBody"/>, each body's cut flag set when it was cut.</summary>
    internal static ReviewPurpose Bound(ReviewPurpose given) => given with
    {
        Title = Cut(given.Title, MaxTitle),
        Description = Cut(given.Description, MaxBody),
        IssueTitle = Cut(given.IssueTitle, MaxTitle),
        IssueBody = Cut(given.IssueBody, MaxBody),
        DescriptionCut = given.DescriptionCut || given.Description?.Length > MaxBody,
        IssueBodyCut = given.IssueBodyCut || given.IssueBody?.Length > MaxBody,
    };

    /// <summary>
    /// Scout's sentences on the inputs, counts only and never a path or a word of the pull request: the removals', then the
    /// inputs that could not be read; a sentence appears only when its count is above zero or its flag is set.
    /// </summary>
    internal IReadOnlyList<string> Notes()
    {
        List<string> notes = [.. RemovalNotes()];
        if (Purpose?.IssueUnread == true)
        {
            notes.Add("The linked issue could not be read.");
        }

        if (Rules.Unreadable)
        {
            notes.Add("The project's rule files could not be read.");
        }

        return notes;
    }

    /// <summary>The sentences of <see cref="Notes"/> about what the pull request removed, each counted over every removal and agreeing with its number.</summary>
    internal IReadOnlyList<string> RemovalNotes()
    {
        var (deleted, deletedTests, leftBehind, emptied, declarations) = Removed.Counts ?? RemovedCounts.Of(Removed.Entries);
        var withoutDiff = Removed.TestFilesWithoutDiff;
        (bool Shown, string Sentence)[] notes =
        [
            (deleted > 0, deleted == 1
                ? deletedTests == 1 ? "The pull request deletes 1 file, a test file." : "The pull request deletes 1 file, not a test file."
                : Say($"The pull request deletes {deleted} files, {deletedTests} of them {(deletedTests == 1 ? "a test file" : "test files")}.")),
            (leftBehind > 0, leftBehind == 1
                ? "1 deleted or emptied test file leaves code named like it in the repository."
                : Say($"{leftBehind} deleted or emptied test files leave code named like them in the repository.")),
            (emptied > 0, emptied == 1 ? "1 test file is emptied." : Say($"{emptied} test files are emptied.")),
            (declarations > 0, declarations == 1
                ? "1 test file loses more test declarations than it gains."
                : Say($"{declarations} test files lose more test declarations than they gain.")),
            (withoutDiff > 0, withoutDiff == 1
                ? "1 changed test file had no diff to count removed tests in."
                : Say($"{withoutDiff} changed test files had no diff to count removed tests in.")),
            (Removed.RepositoryNotSearched, "Scout could not look in the repository for the code the deleted tests covered."),
        ];
        return [.. notes.Where(n => n.Shown).Select(n => n.Sentence)];

        static string Say(FormattableString sentence) => sentence.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>The text cut at <paramref name="cap"/> characters, one fewer when the cut would split a surrogate pair.</summary>
    [return: NotNullIfNotNull(nameof(text))]
    internal static string? Cut(string? text, int cap) =>
        text is null || text.Length <= cap ? text : text[..(char.IsHighSurrogate(text[cap - 1]) ? cap - 1 : cap)];
}
