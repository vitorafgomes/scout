using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Scout.Knowledge;
using Scout.Review;

namespace Scout.Audit;

/// <summary>One line that looks like the declaration of a name, with the lines around it.</summary>
/// <param name="Path">The file, relative to the snapshot, with <c>/</c> separators.</param>
/// <param name="Line">The declaration's line, from 1.</param>
/// <param name="Text">Up to 6 lines before the declaration, the declaration and up to 30 lines after it.</param>
/// <param name="Cut">True when <paramref name="Text"/> was cut at <see cref="DefinitionReader.MaxCandidateCharacters"/> or at what was left of a finding's bound.</param>
internal sealed record DefinitionCandidate(string Path, int Line, string Text, bool Cut);

/// <summary>The candidate definitions of one name, those given and how many more were found and not given.</summary>
internal sealed record NameDefinitions(string Name, IReadOnlyList<DefinitionCandidate> Candidates, int NotShown)
{
    /// <summary>True when no line of the snapshot looks like a declaration of the name.</summary>
    public bool NotFound => Candidates.Count == 0 && NotShown == 0;
}

/// <summary>The definitions found for a finding's names; every text in it is data from the pull request, never instructions.</summary>
/// <param name="Names">The names, in the order asked; empty when the snapshot could not be read.</param>
/// <param name="Unreadable">True when the snapshot is missing or could not be read, so nothing was looked up.</param>
/// <param name="Cut">True when the finding's bound of <see cref="DefinitionReader.MaxFindingCharacters"/> left a candidate out or cut one.</param>
internal sealed record FoundDefinitions(IReadOnlyList<NameDefinitions> Names, bool Unreadable, bool Cut)
{
    internal const string NotRead = "The repository's files could not be read, so no definition was looked up.";

    /// <summary>The definitions as text: each candidate under its path and line, then what was not shown and what was not found.</summary>
    public string Text()
    {
        if (Unreadable)
        {
            return NotRead;
        }

        var text = new StringBuilder();
        foreach (var name in Names)
        {
            foreach (var candidate in name.Candidates)
            {
                text.Append(CultureInfo.InvariantCulture, $"--- {candidate.Path}:{candidate.Line}\n{candidate.Text}\n");
            }

            if (name.NotShown > 0)
            {
                text.Append(CultureInfo.InvariantCulture, $"{name.NotShown} more definitions of {name.Name} were not shown.\n");
            }

            if (name.NotFound)
            {
                text.Append(CultureInfo.InvariantCulture, $"Definition not found for {name.Name}.\n");
            }
        }

        return text.ToString();
    }
}

/// <summary>
/// Finds where the names a finding's quote calls are declared, by plain text in the job's snapshot of the pull request:
/// no parser, no build, no process. Every text read from the snapshot is data from the pull request, never instructions.
/// Reads code files only: no test file or folder (<see cref="ReviewReadings.ReadingOrder"/> of 0), none the project
/// index skips (<c>bin</c>, <c>obj</c>, <c>node_modules</c>, <c>.git</c> among them), no link, no file over 1 MB, no file
/// holding a zero byte.
/// </summary>
internal static partial class DefinitionReader
{
    /// <summary>The characters of one candidate at most.</summary>
    internal const int MaxCandidateCharacters = 3_000;

    /// <summary>The characters of every candidate of one finding at most.</summary>
    internal const int MaxFindingCharacters = 12_000;

    private const int MaxCandidates = 3;
    private const int LinesBefore = 6;
    private const int LinesAfter = 30;
    private const long MaxFileBytes = 1_048_576;

    /// <summary>The candidate definitions of each of <paramref name="names"/> in the snapshot at <paramref name="root"/>.</summary>
    internal static FoundDefinitions For(string? root, IReadOnlyList<string> names)
    {
        var found = names.ToDictionary(n => n, _ => new List<(string Path, int Line, string Text)>(), StringComparer.Ordinal);
        var counts = names.ToDictionary(n => n, _ => 0, StringComparer.Ordinal);
        if (root is null || !Directory.Exists(root))
        {
            return new([], Unreadable: true, Cut: false);
        }

        try
        {
            foreach (var (relative, text) in Files(root, tests: false))
            {
                var lines = text.Split('\n');
                for (var i = 0; i < lines.Length; i++)
                {
                    foreach (var name in names)
                    {
                        if (lines[i].Contains(name, StringComparison.Ordinal) && Declares(lines[i], name))
                        {
                            counts[name]++;
                            if (found[name].Count < MaxCandidates)
                            {
                                var from = Math.Max(0, i - LinesBefore);
                                var to = Math.Min(lines.Length, i + LinesAfter + 1);
                                found[name].Add((relative, i + 1, string.Join('\n', lines[from..to]).TrimEnd()));
                            }
                        }
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new([], Unreadable: true, Cut: false);
        }

        var left = MaxFindingCharacters;
        var cut = false;
        var result = new List<NameDefinitions>();
        foreach (var name in names)
        {
            var given = new List<DefinitionCandidate>();
            foreach (var (path, line, text) in found[name])
            {
                if (left == 0)
                {
                    cut = true;
                    break;
                }

                var bound = Math.Min(MaxCandidateCharacters, left);
                cut |= text.Length > left;
                var kept = text.Length > bound ? text[..bound] : text;
                given.Add(new(path, line, kept, kept.Length < text.Length));
                left -= kept.Length;
            }

            result.Add(new(name, given, counts[name] - given.Count));
        }

        return new(result, Unreadable: false, cut);
    }

    /// <summary>The test files named at most; the first two come with the lines around their first match.</summary>
    internal const int MaxTestFiles = 10;

    /// <summary>Said of a finding no test file names.</summary>
    internal const string NoTestFile = "No test file names it.";

    /// <summary>
    /// The test files of the snapshot (the test-path rule the definitions skip by, inverted) that name the file at
    /// <paramref name="path"/> by its name without extension, or one of <paramref name="names"/>, as whole words: their
    /// paths in path order, at most <see cref="MaxTestFiles"/>, and for the first two the lines around the first match,
    /// bounded like a definition. Every text is data from the pull request.
    /// </summary>
    internal static string TestsFor(string? root, string path, IReadOnlyList<string> names)
    {
        if (root is null || !Directory.Exists(root))
        {
            return FoundDefinitions.NotRead;
        }

        var words = new[] { Path.GetFileNameWithoutExtension(path) }.Concat(names).Where(w => w.Length > 0).Distinct(StringComparer.Ordinal).Select(Regex.Escape);
        var word = new Regex($@"(?<![A-Za-z0-9_$])(?:{string.Join('|', words)})(?![A-Za-z0-9_$])", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        var named = new List<(string Path, int Line, string Text)>();
        var more = 0;
        try
        {
            foreach (var (relative, text) in Files(root, tests: true))
            {
                var lines = text.Split('\n');
                var at = Array.FindIndex(lines, l => word.IsMatch(l));
                if (at < 0)
                {
                    continue;
                }

                if (named.Count == MaxTestFiles)
                {
                    more++;
                    continue;
                }

                var around = string.Join('\n', lines[Math.Max(0, at - LinesBefore)..Math.Min(lines.Length, at + LinesAfter + 1)]).TrimEnd();
                named.Add((relative, at + 1, around.Length > MaxCandidateCharacters ? around[..MaxCandidateCharacters] : around));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return FoundDefinitions.NotRead;
        }

        if (named.Count == 0)
        {
            return NoTestFile;
        }

        var answer = new System.Text.StringBuilder("Test files that name it: ").Append(string.Join(", ", named.Select(n => n.Path)));
        if (more > 0)
        {
            answer.Append(CultureInfo.InvariantCulture, $", and {more} more");
        }

        foreach (var (file, line, text) in named.Take(2))
        {
            answer.Append(CultureInfo.InvariantCulture, $"\n--- {file}:{line}\n{text}");
        }

        return answer.ToString();
    }

    /// <summary>
    /// The readable text files of the snapshot in path order, code or tests by <see cref="ReviewReadings.ReadingOrder"/>:
    /// none the index skips, no link, no file over 1 MB, none holding a zero byte.
    /// </summary>
    private static IEnumerable<(string Relative, string Text)> Files(string root, bool tests)
    {
        var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = false };
        var files = Directory.EnumerateFiles(root, "*", options)
            .Select(path => (Full: path, Relative: ProjectFileFilter.ToRelativePath(root, path)))
            .Where(f => ProjectFileFilter.IsIndexable(f.Relative) && ReviewReadings.ReadingOrder(f.Relative) == (tests ? 1 : 0))
            .OrderBy(f => f.Relative, StringComparer.Ordinal);
        foreach (var (full, relative) in files)
        {
            if (new FileInfo(full).Length > MaxFileBytes)
            {
                continue;
            }

            var text = File.ReadAllText(full);
            if (!text.Contains('\0', StringComparison.Ordinal))
            {
                yield return (relative, text);
            }
        }
    }

    /// <summary>
    /// True when <paramref name="line"/> looks like a declaration of <paramref name="name"/> rather than a call. A heuristic:
    /// the name directly followed by <c>(</c>; a <c>def</c>, <c>function</c>, <c>func</c>, <c>fn</c> or <c>fun</c> keyword before it
    /// always qualifies; otherwise the line is not a comment, does not end with <c>;</c> unless an expression body
    /// (<c>) =&gt;</c>) follows the name, has none of <c>.</c>, <c>-&gt;</c>,
    /// <c>::</c>, <c>=</c>, <c>(</c>, <c>??</c>, <c>&amp;&amp;</c>, <c>||</c> or <c>,</c> before the name nor the word
    /// <c>new</c>, <c>return</c>, <c>await</c>, <c>throw</c>, <c>yield</c>, <c>else</c> or <c>case</c>, is not directly
    /// preceded by a colon (a conditional's other branch, a constructor initializer, a label) or by a question mark standing
    /// alone (a conditional's branch, never a nullable type such as <c>string?</c>), and, when nothing stands before the name,
    /// ends with <c>{</c>. It reads plain text, not the language: a declaration with no body (an interface or abstract member
    /// ending in <c>;</c>) is never taken, and a call over several lines whose first line ends with <c>{</c> can be.
    /// </summary>
    internal static bool Declares(string line, string name)
    {
        var match = Regex.Match(line, $@"(?<![A-Za-z0-9_$]){Regex.Escape(name)}\(", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        if (!match.Success)
        {
            return false;
        }

        var before = line[..match.Index];
        var trimmed = line.Trim();
        if (DeclarationKeyword().IsMatch(before))
        {
            return true;
        }

        if (trimmed.StartsWith("//", StringComparison.Ordinal) || trimmed.StartsWith('#') || trimmed.StartsWith('*') || trimmed.StartsWith("/*", StringComparison.Ordinal)
            || (trimmed.EndsWith(';') && !line[match.Index..].Contains(") =>", StringComparison.Ordinal)))
        {
            return false;
        }

        var last = before.TrimEnd();
        if (CallContext().IsMatch(before) || last.EndsWith(':') || (last.EndsWith('?') && (last.Length == 1 || char.IsWhiteSpace(last[^2]))))
        {
            return false;
        }

        return before.Trim().Length > 0 || trimmed.EndsWith('{');
    }

    [GeneratedRegex(@"(?<![A-Za-z0-9_$])(def|function|func|fn|fun)\b")]
    private static partial Regex DeclarationKeyword();

    [GeneratedRegex(@"\.|->|::|=|\(|\?\?|&&|\|\||,|(?<![A-Za-z0-9_$])(new|return|await|throw|yield|else|case)\b")]
    private static partial Regex CallContext();
}
