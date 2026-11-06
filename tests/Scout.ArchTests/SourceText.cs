namespace Scout.ArchTests;

/// <summary>What the source-scanning rules share: which files are source, and how to see only the code, or only the comments.</summary>
internal static class SourceText
{
    private static readonly string[] SkippedSegments = ["obj", "bin", "node_modules", "dist", ".angular"];

    private readonly record struct Span(int Start, int End, bool IsComment);

    /// <summary>Every *.cs under the named roots, minus build output and anything a package manager owns.</summary>
    internal static IEnumerable<string> SourceFiles(params string[] roots) => Files(roots, "*.cs");

    internal static List<string> TestProjects() =>
        Files(["tests"], "*.csproj").OrderBy(x => x, StringComparer.Ordinal).ToList();

    internal static IEnumerable<string> Files(string[] roots, string pattern)
    {
        foreach (var rootName in roots)
        {
            var root = Path.Combine(ArchitectureTests.SolutionRoot(), rootName);

            if (Directory.Exists(root) is false)
            {
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(root, pattern, SearchOption.AllDirectories))
            {
                if (SkippedSegments.Any(segment => file.Contains($"{Path.DirectorySeparatorChar}{segment}{Path.DirectorySeparatorChar}", StringComparison.Ordinal)))
                {
                    continue;
                }

                yield return file;
            }
        }
    }

    internal static string Relative(string file) =>
        Path.GetRelativePath(ArchitectureTests.SolutionRoot(), file).Replace(Path.DirectorySeparatorChar, '/');

    /// <summary>Blanks comments and string or char literals, keeping every newline so line numbers survive.</summary>
    internal static string StripLiteralsAndComments(string source)
    {
        var output = source.ToCharArray();

        foreach (var span in Spans(source))
        {
            BlankRange(output, span.Start, span.End);
        }

        return new string(output);
    }

    /// <summary>Blanks code and literals, keeping only the comments and every newline.</summary>
    internal static string CommentsOnly(string source)
    {
        var output = source.ToCharArray();
        var cursor = 0;

        foreach (var span in Spans(source))
        {
            BlankRange(output, cursor, span.IsComment ? span.Start : span.End);
            cursor = span.End;
        }

        BlankRange(output, cursor, source.Length);
        return new string(output);
    }

    private static void BlankRange(char[] output, int from, int to)
    {
        for (var k = from; k < to && k < output.Length; k++)
        {
            if (output[k] is not '\n') { output[k] = ' '; }
        }
    }

    /// <summary>Comments and literals in source order; End is exclusive. Raw string literals close on a quote run of the same length as the one that opened them.</summary>
    private static IEnumerable<Span> Spans(string source)
    {
        for (var i = 0; i < source.Length; i++)
        {
            var c = source[i];

            if (c is '/' && i + 1 < source.Length && source[i + 1] is '/')
            {
                var start = i;
                while (i < source.Length && source[i] is not '\n') { i++; }
                yield return new Span(start, i, true);
                continue;
            }

            if (c is '/' && i + 1 < source.Length && source[i + 1] is '*')
            {
                var start = i;
                i += 2;
                while (i + 1 < source.Length && (source[i] is '*' && source[i + 1] is '/') is false) { i++; }
                i++;
                yield return new Span(start, Math.Min(i + 1, source.Length), true);
                continue;
            }

            if (c is '"' && i + 2 < source.Length && source[i + 1] is '"' && source[i + 2] is '"')
            {
                var start = i;
                var quotes = 0;
                while (i < source.Length && source[i] is '"') { quotes++; i++; }
                var close = QuoteRun(source, i, quotes);
                i = close < 0 ? source.Length : close + quotes;
                yield return new Span(start, i, false);
                i--;
                continue;
            }

            if (c is '\'' or '"')
            {
                var start = i;
                var quote = c;
                var verbatim = (i > 0 && source[i - 1] is '@') || (i > 1 && source[i - 1] is '$' && source[i - 2] is '@');
                i++;

                while (i < source.Length)
                {
                    if (verbatim is false && source[i] is '\\') { i += 2; continue; }
                    if (source[i] == quote)
                    {
                        if (verbatim && i + 1 < source.Length && source[i + 1] == quote) { i += 2; continue; }
                        break;
                    }

                    i++;
                }

                yield return new Span(start, Math.Min(i + 1, source.Length), false);
            }
        }
    }

    private static int QuoteRun(string source, int from, int length)
    {
        for (var j = from; j < source.Length;)
        {
            if (source[j] is not '"') { j++; continue; }

            var run = 0;
            while (j + run < source.Length && source[j + run] is '"') { run++; }

            if (run == length) { return j; }

            j += run;
        }

        return -1;
    }
}
