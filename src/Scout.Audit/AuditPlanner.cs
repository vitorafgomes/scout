using System.Globalization;
using Scout.Knowledge;

namespace Scout.Audit;

/// <summary>
/// One batch of the plan: the area it covers, its files as repository-relative paths, their size in characters, the
/// files longer than a read returns, which the model can only read in part, and, for a batch no turn can read, why it is
/// kept not covered from the start.
/// </summary>
internal sealed record AuditBatchPlan(string Area, IReadOnlyList<string> Files, long Characters, IReadOnlyList<string>? Partial = null, string? Failure = null);

/// <summary>
/// Cuts a snapshot's files into the audit's batches, by Scout and never by the model, in the same order every time: the
/// manifests, the configuration and the top-level files first; then each folder, whole when it fits the caps, otherwise
/// split by its subfolders, with neighbouring small folders packed together up to the caps; test and generated folders
/// last. Only the files the project index reads count, so build output, dependencies and credentials files never enter.
/// </summary>
internal static class AuditPlanner
{
    internal const string ProjectFilesArea = "manifests, configuration and top-level files";

    /// <summary>Characters of a file the GitHub plugin's read returns; a longer file is cut there.</summary>
    internal const int ReadCap = 20_000;

    private static readonly HashSet<string> ManifestNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "package.json", "go.mod", "pom.xml", "build.gradle", "build.gradle.kts", "settings.gradle", "Cargo.toml",
        "pyproject.toml", "requirements.txt", "Gemfile", "composer.json", "Dockerfile", "Makefile", "CMakeLists.txt",
        "tsconfig.json", "angular.json",
    };

    private static readonly HashSet<string> ManifestExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".csproj", ".fsproj", ".vbproj", ".props", ".targets", ".sln", ".slnx",
    };

    private static readonly HashSet<string> LastFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "test", "tests", "spec", "specs", "__tests__", "testing", "e2e", "generated", "__generated__",
    };

    public static IReadOnlyList<AuditBatchPlan> Plan(string root, int maxFiles, long maxCharacters)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        return Plan(
            [.. Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                .Select(path => new Entry(ProjectFileFilter.ToRelativePath(root, path), new FileInfo(path).Length))
                .Where(entry => ProjectFileFilter.IsIndexable(entry.Path))],
            maxFiles,
            maxCharacters);
    }

    /// <summary>Plans exactly the given files, in the order and under the caps of a folder's plan; nothing in the list is left out.</summary>
    public static IReadOnlyList<AuditBatchPlan> Plan(IReadOnlyList<Entry> entries, int maxFiles, long maxCharacters)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var caps = new Caps(Math.Max(1, maxFiles), Math.Max(1, maxCharacters));
        var files = entries.OrderBy(entry => entry.Path, StringComparer.Ordinal).ToList();

        var first = files.Where(IsProjectFile).ToList();
        var rest = files.Except(first).ToList();
        var last = rest.Where(IsTestOrGenerated).ToList();
        var middle = rest.Except(last).ToList();
        return [.. Chunks(ProjectFilesArea, first, caps), .. Folders(string.Empty, middle, caps), .. Folders(string.Empty, last, caps)];
    }

    internal static bool IsProjectFile(Entry entry)
    {
        var name = Path.GetFileName(entry.Path);
        return !entry.Path.Contains('/', StringComparison.Ordinal)
            || entry.Path.StartsWith(".github/", StringComparison.Ordinal)
            || ManifestNames.Contains(name)
            || ManifestExtensions.Contains(Path.GetExtension(name));
    }

    internal static bool IsTestOrGenerated(Entry entry)
    {
        var segments = entry.Path.Split('/');
        var name = segments[^1];
        return segments[..^1].Any(s => LastFolders.Contains(s) || s.EndsWith("Tests", StringComparison.OrdinalIgnoreCase) || s.EndsWith(".Test", StringComparison.OrdinalIgnoreCase))
            || name.Contains(".spec.", StringComparison.OrdinalIgnoreCase)
            || name.Contains(".test.", StringComparison.OrdinalIgnoreCase)
            || name.Contains(".generated.", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith("_test.go", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".designer.cs", StringComparison.OrdinalIgnoreCase)
            || (name.StartsWith("test_", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".py", StringComparison.OrdinalIgnoreCase));
    }

    private static IEnumerable<AuditBatchPlan> Folders(string prefix, List<Entry> files, Caps caps)
    {
        AuditBatchPlan? pending = null;
        var groups = files
            .GroupBy(entry => prefix + entry.Path[prefix.Length..].Split('/')[0])
            .OrderBy(group => group.Key, StringComparer.Ordinal);
        foreach (var group in groups)
        {
            foreach (var batch in Split(group.Key, [.. group], caps))
            {
                if (pending is not null && caps.Fit(pending.Files.Count + batch.Files.Count, pending.Characters + batch.Characters))
                {
                    pending = new AuditBatchPlan(
                        $"{pending.Area}, {batch.Area}",
                        [.. pending.Files, .. batch.Files],
                        pending.Characters + batch.Characters,
                        [.. pending.Partial ?? [], .. batch.Partial ?? []]);
                    continue;
                }

                if (pending is not null)
                {
                    yield return pending;
                }

                pending = batch;
            }
        }

        if (pending is not null)
        {
            yield return pending;
        }
    }

    private static IEnumerable<AuditBatchPlan> Split(string folder, List<Entry> files, Caps caps)
    {
        if (caps.Fit(files.Count, files.Sum(f => f.Size)))
        {
            return [Batch(folder, files)];
        }

        var prefix = folder + "/";
        var direct = files.Where(f => !f.Path[prefix.Length..].Contains('/', StringComparison.Ordinal)).ToList();
        return Folders(prefix, files.Except(direct).ToList(), caps).Concat(Chunks(folder, direct, caps));
    }

    private static IEnumerable<AuditBatchPlan> Chunks(string area, List<Entry> files, Caps caps)
    {
        var chunks = new List<List<Entry>>();
        foreach (var entry in files)
        {
            var current = chunks.Count == 0 ? null : chunks[^1];
            if (current is null || !caps.Fit(current.Count + 1, current.Sum(f => f.Size) + entry.Size))
            {
                chunks.Add([entry]);
            }
            else
            {
                current.Add(entry);
            }
        }

        return chunks.Select((chunk, i) => Batch(
            chunks.Count == 1 ? area : string.Create(CultureInfo.InvariantCulture, $"{area} ({i + 1}/{chunks.Count})"), chunk));
    }

    private static AuditBatchPlan Batch(string area, List<Entry> files) =>
        new(area, [.. files.Select(f => f.Path)], files.Sum(f => f.Size), [.. files.Where(f => f.Size > ReadCap).Select(f => f.Path)]);

    internal sealed record Entry(string Path, long Size);

    private sealed record Caps(int Files, long Characters)
    {
        public bool Fit(int files, long characters) => files <= Files && characters <= Characters;
    }
}
