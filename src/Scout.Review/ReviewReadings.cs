namespace Scout.Review;

/// <summary>One focused reading of a review batch: its name and the task paragraph its request carries.</summary>
/// <param name="Name">The reading's name, such as <see cref="ReviewReadings.Removed"/>.</param>
/// <param name="Task">The paragraph that tells the model what this reading looks for.</param>
public sealed record ReviewReading(string Name, string Task);

/// <summary>
/// The focused readings a review batch gets besides its general reading, and when each applies: the removed reading when
/// the batch's patches remove lines, the requests reading when the pull request's description lists requests. Each is the
/// general request with its task paragraph; nothing else of the request changes. Pure: no model call, no file.
/// </summary>
public static class ReviewReadings
{
    /// <summary>The reading of what the patches remove.</summary>
    public const string Removed = "removed";

    /// <summary>The reading of the description's requests, one at a time.</summary>
    public const string Requests = "requests";

    /// <summary>The task of the removed reading.</summary>
    public const string RemovedTask =
        "This reading looks only at what the patches remove, the lines that start with '-', above all removed tests and removed checks. For each removal, read the code at this pull request's head and decide whether the code or the behaviour the removed lines tested or guarded is still there without them. Report each thing the removal leaves unprotected, its evidence the removed lines as they read, without the leading '-'.";

    /// <summary>The task of the requests reading.</summary>
    public const string RequestsTask =
        "This reading takes the requests of the pull request's description one at a time. For each request, find in these patches and at this pull request's head the code that fulfils it, and decide whether every place the request applies to is covered, sibling operations of the one that changed included. Report each request that is only partly met, naming the request in the finding.";

    /// <summary>
    /// The focused readings of a batch whose files are <paramref name="files"/>, for a pull request whose description is
    /// <paramref name="description"/>, counting only the files that are code or tests (<see cref="IsCodeOrTest"/>): the
    /// removed reading when the patch of one of them removes a line, the requests reading when one of them is in the batch
    /// and the description lists a request; in that order. A batch of documentation, configuration or build files gets neither.
    /// </summary>
    public static IReadOnlyList<ReviewReading> For(IEnumerable<(string Path, string? Patch)> files, string? description)
    {
        ArgumentNullException.ThrowIfNull(files);
        var code = files.Where(f => IsCodeOrTest(f.Path)).ToList();
        var readings = new List<ReviewReading>();
        if (code.Exists(f => RemovesLines(f.Patch)))
        {
            readings.Add(new ReviewReading(Removed, RemovedTask));
        }

        if (code.Count > 0 && PullRequestDescription.Read(description).Requests.Count > 0)
        {
            readings.Add(new ReviewReading(Requests, RequestsTask));
        }

        return readings;
    }

    /// <summary>
    /// Where a changed file comes in a review's reading order: 0 source code, 1 tests, 2 build and configuration files,
    /// 3 documentation. Documentation is a file with a documentation extension, or any file under a <c>docs</c>,
    /// <c>context</c> or <c>.claude</c> folder at the repository's root; a test is a file under a test folder, in a project whose name ends
    /// <c>.Tests</c>, or named as a test; the rest is code, its build and configuration files after the tests.
    /// </summary>
    public static int ReadingOrder(string path)
    {
        var segments = path.Split('/');
        var name = segments[^1];
        var folders = segments[..^1];
        if (DocumentationExtensions.Contains(Path.GetExtension(name)) || (folders.Length > 0 && DocumentationFolders.Contains(folders[0])))
        {
            return 3;
        }

        if (folders.Any(f => TestFolders.Contains(f) || f.EndsWith(".Tests", StringComparison.OrdinalIgnoreCase))
            || name.Contains(".test.", StringComparison.OrdinalIgnoreCase) || name.Contains(".spec.", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith("Tests.cs", StringComparison.Ordinal))
        {
            return 1;
        }

        return ConfigurationExtensions.Contains(Path.GetExtension(name)) || name.Equals("Dockerfile", StringComparison.OrdinalIgnoreCase) ? 2 : 0;
    }

    private static readonly HashSet<string> DocumentationExtensions = new([".md", ".mdx", ".txt", ".rst", ".adoc", ".html"], StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> DocumentationFolders = new(["docs", "context", ".claude"], StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> TestFolders = new(["test", "tests", "spec", "e2e", "__tests__"], StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> ConfigurationExtensions = new([".json", ".yml", ".yaml", ".toml", ".xml", ".props", ".lock"], StringComparer.OrdinalIgnoreCase);

    /// <summary>True when <paramref name="path"/> is source code or a test in the <see cref="ReadingOrder"/>.</summary>
    public static bool IsCodeOrTest(string path) => ReadingOrder(path) <= 1;

    /// <summary>True when <paramref name="patch"/> removes at least one line inside a hunk.</summary>
    public static bool RemovesLines(string? patch) => DiffQuote.RemovedLines(patch).Count > 0;
}
