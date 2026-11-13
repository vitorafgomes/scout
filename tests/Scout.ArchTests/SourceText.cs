namespace Scout.ArchTests;

/// <summary>
/// Finds the repository files a source rule reads and splits a C# text into code, comments and literals,
/// so a rule can look at one of the three without being fooled by the other two.
/// </summary>
internal static class SourceText
{
    private enum Region : byte
    {
        Code,
        Comment,
        Literal,
    }

    private static readonly HashSet<string> ToolOwnedFolders = new(StringComparer.Ordinal) { "bin", "obj", "node_modules", "dist", ".angular" };

    /// <summary>The C# files below the given repository folders.</summary>
    internal static IEnumerable<string> CSharpUnder(params string[] folders) => FilesNamed("*.cs", folders);

    /// <summary>
    /// The files whose name fits the pattern below the given repository folders, leaving out what a build or a
    /// package manager wrote there; a folder that does not exist contributes nothing.
    /// </summary>
    internal static IEnumerable<string> FilesNamed(string pattern, params string[] folders) =>
        folders
            .Select(folder => Path.Combine(ArchitectureTests.SolutionRoot(), folder))
            .Where(Directory.Exists)
            .SelectMany(folder => Directory.EnumerateFiles(folder, pattern, SearchOption.AllDirectories))
            .Where(path => WrittenByATool(path) is false);

    /// <summary>Every project file under tests, in ordinal order.</summary>
    internal static List<string> TestProjectFiles() =>
        [.. FilesNamed("*.csproj", "tests").Order(StringComparer.Ordinal)];

    /// <summary>The path from the repository root, with forward slashes on every platform.</summary>
    internal static string RepoPath(string path) =>
        Path.GetRelativePath(ArchitectureTests.SolutionRoot(), path).Replace('\\', '/');

    /// <summary>The text with every comment and every string or char literal turned into spaces; newlines stay, so a line number still points at the same line.</summary>
    internal static string CodeOnly(string text) => Keep(text, Region.Code);

    /// <summary>The text with everything but the comments turned into spaces; newlines stay.</summary>
    internal static string CommentText(string text) => Keep(text, Region.Comment);

    private static bool WrittenByATool(string path)
    {
        var folders = RepoPath(Path.GetDirectoryName(path) ?? string.Empty).Split('/');
        return folders.Any(ToolOwnedFolders.Contains);
    }

    private static string Keep(string text, Region wanted)
    {
        var regions = Classify(text);
        var kept = new char[text.Length];

        for (var at = 0; at < text.Length; at++)
        {
            kept[at] = regions[at] == wanted || text[at] == '\n' ? text[at] : ' ';
        }

        return new string(kept);
    }

    private static Region[] Classify(string text)
    {
        var regions = new Region[text.Length];
        var at = 0;

        while (at < text.Length)
        {
            var (region, length) = NextToken(text, at);
            Array.Fill(regions, region, at, length);
            at += length;
        }

        return regions;
    }

    private static (Region Region, int Length) NextToken(string text, int at)
    {
        var rest = text.AsSpan(at);

        if (rest.StartsWith("//", StringComparison.Ordinal))
        {
            var newline = rest.IndexOf('\n');
            return (Region.Comment, newline < 0 ? rest.Length : newline);
        }

        if (rest.StartsWith("/*", StringComparison.Ordinal))
        {
            var closer = rest[2..].IndexOf("*/", StringComparison.Ordinal);
            return (Region.Comment, closer < 0 ? rest.Length : closer + 4);
        }

        if (rest.StartsWith("\"\"\"", StringComparison.Ordinal))
        {
            return (Region.Literal, RawLiteralLength(rest));
        }

        if (rest[0] is '"' or '\'')
        {
            return (Region.Literal, QuotedLiteralLength(rest, IsVerbatim(text.AsSpan(0, at))));
        }

        return (Region.Code, 1);
    }

    private static bool IsVerbatim(ReadOnlySpan<char> before) =>
        before.EndsWith('@') || before.EndsWith("@$", StringComparison.Ordinal);

    private static int QuotedLiteralLength(ReadOnlySpan<char> rest, bool verbatim)
    {
        var delimiter = rest[0];
        var k = 1;

        while (k < rest.Length)
        {
            var current = rest[k];

            if (current == '\\' && verbatim is false)
            {
                k += 2;
            }
            else if (current != delimiter)
            {
                k++;
            }
            else if (verbatim && k + 1 < rest.Length && rest[k + 1] == delimiter)
            {
                k += 2;
            }
            else
            {
                return k + 1;
            }
        }

        return rest.Length;
    }

    private static int RawLiteralLength(ReadOnlySpan<char> rest)
    {
        var opener = QuoteRunAt(rest, 0);
        var k = opener;

        while (k < rest.Length)
        {
            var next = rest[k..].IndexOf('"');

            if (next < 0)
            {
                break;
            }

            var run = QuoteRunAt(rest, k + next);

            if (run == opener)
            {
                return k + next + run;
            }

            k += next + run;
        }

        return rest.Length;
    }

    private static int QuoteRunAt(ReadOnlySpan<char> rest, int from)
    {
        var run = 0;

        while (from + run < rest.Length && rest[from + run] == '"')
        {
            run++;
        }

        return run;
    }
}
