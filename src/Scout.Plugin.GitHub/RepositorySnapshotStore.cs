using System.Formats.Tar;
using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using Scout.Plugin.GitHub.Abstractions;

namespace Scout.Plugin.GitHub;

/// <summary>
/// One repository at one commit, extracted under Scout's data folder so a review or an audit reads every file
/// locally: the tree under <c>Root</c>, the commit it came from, and how much it holds.
/// </summary>
internal sealed record RepositorySnapshot(
    string Owner,
    string Repo,
    string Reference,
    string Sha,
    bool IsDefaultBranch,
    string Root,
    DateTimeOffset TakenAt,
    int FileCount,
    long Bytes)
{
    /// <summary>The first seven characters of the commit, for the answers that say where a text came from.</summary>
    public string ShortSha => Sha[..Math.Min(7, Sha.Length)];

    /// <summary>The owner/repo at the short commit, for the answers that say where a text came from.</summary>
    public string Label => $"{Owner}/{Repo} at {Reference} ({ShortSha})";
}

/// <summary>
/// Keeps repository snapshots under <c>Scout/github-snapshots/&lt;owner&gt;__&lt;repo&gt;/&lt;sha&gt;</c>, each described
/// by a <c>&lt;sha&gt;.json</c> beside its folder so the tree holds only the repository's own files: extracts a
/// tarball GitHub streamed, regular files only, no path that climbs out of the tree, no <c>.git</c>, within a byte
/// and a file cap; reads a file as text, lists the files and searches them for a review that needs the whole
/// repository rather than the API's 100-file pages. Snapshots older than <see cref="MaxAge"/> go when a new one
/// of the same repository lands.
/// </summary>
internal sealed class RepositorySnapshotStore(string directory, long maxBytes, TimeProvider time) : IRepositorySnapshots
{
    internal const long DefaultMaxBytes = 200L * 1024 * 1024;
    internal const int MaxFiles = 20_000;
    internal const int MaxTextBytes = 1024 * 1024;
    internal const int SearchHits = 30;
    internal static readonly TimeSpan MaxAge = TimeSpan.FromDays(7);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    /// <summary>The folder the snapshots live in.</summary>
    internal string Directory { get; } = string.IsNullOrWhiteSpace(directory) ? throw new ArgumentException("A snapshots folder is required.", nameof(directory)) : directory;

    internal long MaxBytes { get; } = maxBytes > 0 ? maxBytes : DefaultMaxBytes;

    /// <summary>The newest snapshot of the repository at <paramref name="reference"/>, or at its default branch when empty; null when none.</summary>
    public RepositorySnapshot? Find(string owner, string repo, string? reference)
    {
        var wanted = (reference ?? string.Empty).Trim();
        return All(owner, repo)
            .Where(s => wanted.Length == 0 ? s.IsDefaultBranch : string.Equals(s.Reference, wanted, StringComparison.OrdinalIgnoreCase) || string.Equals(s.Sha, wanted, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(s => s.TakenAt)
            .FirstOrDefault();
    }

    /// <inheritdoc/>
    RepositorySnapshotInfo? IRepositorySnapshots.Find(string owner, string repo, string commitOrShortSha)
    {
        var wanted = (commitOrShortSha ?? string.Empty).Trim();
        if (!GitHubNames.IsValidRepository(owner, repo) || wanted.Length < 7)
        {
            return null;
        }

        return All(owner, repo)
            .Where(s => s.Sha.StartsWith(wanted, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(s => s.TakenAt)
            .Select(s => new RepositorySnapshotInfo(s.Owner, s.Repo, s.Sha, s.Root, s.TakenAt))
            .FirstOrDefault();
    }

    /// <summary>The snapshot already taken at <paramref name="sha"/>, or null.</summary>
    public RepositorySnapshot? FindBySha(string owner, string repo, string sha) =>
        All(owner, repo).FirstOrDefault(s => string.Equals(s.Sha, sha, StringComparison.OrdinalIgnoreCase));

    /// <summary>The newest snapshot of the repository at any reference, or null; for a search that names no reference.</summary>
    public RepositorySnapshot? FindNewest(string owner, string repo) =>
        All(owner, repo).OrderByDescending(s => s.TakenAt).FirstOrDefault();

    /// <summary>
    /// Describe an existing snapshot as <paramref name="commit"/>, whose tree it already is (same SHA), so the
    /// reads at that reference find it; the folder is untouched.
    /// </summary>
    public static async Task<RepositorySnapshot> RelabelAsync(RepositorySnapshot snapshot, RepositoryCommit commit, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(commit);
        var relabelled = snapshot with { Reference = commit.Reference, IsDefaultBranch = commit.IsDefaultBranch };
        await WriteMetadataAsync(relabelled, ct).ConfigureAwait(false);
        return relabelled;
    }

    /// <summary>
    /// Extract a gzip tarball of the repository at <paramref name="commit"/> into the store, replacing an earlier
    /// snapshot at the same commit; throws <see cref="SnapshotRefusedException"/> when the tree is over the caps or
    /// an entry's path is not a plain relative path.
    /// </summary>
    public async Task<RepositorySnapshot> ExtractAsync(string owner, string repo, RepositoryCommit commit, Stream tarGz, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(commit);
        ArgumentNullException.ThrowIfNull(tarGz);
        var repoFolder = Path.Combine(Directory, $"{owner.ToLowerInvariant()}__{repo.ToLowerInvariant()}");
        var root = Path.Combine(repoFolder, commit.Sha);
        var staging = root + ".extracting";
        if (System.IO.Directory.Exists(staging))
        {
            System.IO.Directory.Delete(staging, recursive: true);
        }

        System.IO.Directory.CreateDirectory(staging);
        var files = 0;
        long bytes = 0;
        string? topFolder = null;
        try
        {
            await using var gunzip = new GZipStream(tarGz, CompressionMode.Decompress);
            await using var tar = new TarReader(gunzip, leaveOpen: true);
            while (await tar.GetNextEntryAsync(copyData: false, ct).ConfigureAwait(false) is { } entry)
            {
                var isFile = entry.EntryType is TarEntryType.RegularFile or TarEntryType.V7RegularFile;
                if (isFile || entry.EntryType is TarEntryType.Directory)
                {
                    topFolder ??= entry.Name.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                }

                if (!isFile || topFolder is null)
                {
                    continue;
                }

                var relative = RelativePath(entry.Name, topFolder);
                if (relative is null)
                {
                    continue;
                }

                files++;
                bytes += entry.Length;
                if (files > MaxFiles || bytes > MaxBytes)
                {
                    throw new SnapshotRefusedException(
                        $"{owner}/{repo} is over the snapshot cap ({MaxFiles.ToString(CultureInfo.InvariantCulture)} files, {MaxBytes / (1024 * 1024)} MB); narrow to a folder with the GitHub reads instead.");
                }

                var target = Path.Combine(staging, relative);
                System.IO.Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                await using var output = File.Create(target);
                if (entry.DataStream is { } data)
                {
                    await data.CopyToAsync(output, ct).ConfigureAwait(false);
                }
            }

            var snapshot = new RepositorySnapshot(owner, repo, commit.Reference, commit.Sha, commit.IsDefaultBranch, root, time.GetUtcNow(), files, bytes);
            if (System.IO.Directory.Exists(root))
            {
                System.IO.Directory.Delete(root, recursive: true);
            }

            System.IO.Directory.Move(staging, root);
            await WriteMetadataAsync(snapshot, ct).ConfigureAwait(false);
            Purge(repoFolder, keep: commit.Sha);
            return snapshot;
        }
        catch
        {
            if (System.IO.Directory.Exists(staging))
            {
                System.IO.Directory.Delete(staging, recursive: true);
            }

            throw;
        }
    }

    /// <summary>
    /// Writes what a test run of the snapshot would use as <c>&lt;owner&gt;__&lt;repo&gt;.stack.json</c> beside the
    /// repository's snapshots folder, replacing the earlier one: the repository, the commit, the stack, the pinned image,
    /// the version in it and the declared versions, or the refusal, and when it was read.
    /// </summary>
    public Task WriteStackAsync(RepositorySnapshot snapshot, RepositoryStack stack, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(stack);
        var record = new StackRecord(
            $"{snapshot.Owner}/{snapshot.Repo}", snapshot.Sha, stack.Stack, stack.Image, stack.Runs, stack.Declared, stack.Refusal, time.GetUtcNow());
        return File.WriteAllTextAsync(
            Path.Combine(Directory, $"{snapshot.Owner.ToLowerInvariant()}__{snapshot.Repo.ToLowerInvariant()}.stack.json"), JsonSerializer.Serialize(record, Json), ct);
    }

    /// <summary>The file's text, or null when it is missing, binary or over 1 MB; <paramref name="size"/> is its length in bytes.</summary>
    public static string? ReadText(RepositorySnapshot snapshot, string relativePath, out long size)
    {
        size = 0;
        var path = Resolve(snapshot, relativePath);
        if (path is null || !File.Exists(path))
        {
            return null;
        }

        size = new FileInfo(path).Length;
        if (size > MaxTextBytes)
        {
            return null;
        }

        var bytes = File.ReadAllBytes(path);
        return LooksBinary(bytes) ? null : System.Text.Encoding.UTF8.GetString(bytes);
    }

    /// <summary>True when the path is a directory of the snapshot; its entries come back sorted.</summary>
    public static IReadOnlyList<string>? ListDirectory(RepositorySnapshot snapshot, string relativePath)
    {
        var path = relativePath.Length == 0 ? snapshot.Root : Resolve(snapshot, relativePath);
        if (path is null || !System.IO.Directory.Exists(path))
        {
            return null;
        }

        return System.IO.Directory.EnumerateFileSystemEntries(path)
            .Select(Path.GetFileName)
            .Where(n => n is not null)
            .Select(n => n!)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Every file of the snapshot as a repository-relative path with its size.</summary>
    public static IReadOnlyList<RepositoryEntry> Files(RepositorySnapshot snapshot) =>
        System.IO.Directory.EnumerateFiles(snapshot.Root, "*", SearchOption.AllDirectories)
            .Select(p => new RepositoryEntry(Path.GetRelativePath(snapshot.Root, p).Replace('\\', '/'), new FileInfo(p).Length))
            .OrderBy(e => e.Path, StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// The files whose text contains <paramref name="query"/> (case-insensitive), under <paramref name="pathPrefix"/>
    /// and with <paramref name="extension"/> when given, with the matching lines as fragments; the first 30 files.
    /// </summary>
    public static CodeSearchResult Search(RepositorySnapshot snapshot, string query, string pathPrefix, string extension)
    {
        var folder = pathPrefix.Length == 0 ? string.Empty : pathPrefix.TrimEnd('/') + "/";
        var ext = extension.Length == 0 ? string.Empty : extension.StartsWith('.') ? extension : "." + extension;
        var hits = new List<CodeSearchHit>();
        var total = 0;
        foreach (var entry in Files(snapshot))
        {
            if ((folder.Length > 0 && !entry.Path.StartsWith(folder, StringComparison.Ordinal))
                || (ext.Length > 0 && !entry.Path.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
                || entry.Size > MaxTextBytes)
            {
                continue;
            }

            var text = ReadText(snapshot, entry.Path, out _);
            if (text is null || !text.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            total++;
            if (hits.Count < SearchHits)
            {
                var fragments = text.Split('\n')
                    .Where(line => line.Contains(query, StringComparison.OrdinalIgnoreCase))
                    .Take(3)
                    .Select(line => line.TrimEnd('\r'))
                    .ToList();
                hits.Add(new CodeSearchHit(entry.Path, fragments));
            }
        }

        return new CodeSearchResult(total, hits);
    }

    private IEnumerable<RepositorySnapshot> All(string owner, string repo)
    {
        var repoFolder = Path.Combine(Directory, $"{owner.ToLowerInvariant()}__{repo.ToLowerInvariant()}");
        if (!System.IO.Directory.Exists(repoFolder))
        {
            yield break;
        }

        foreach (var meta in System.IO.Directory.EnumerateFiles(repoFolder, "*.json"))
        {
            var folder = meta[..^".json".Length];
            if (!System.IO.Directory.Exists(folder))
            {
                continue;
            }

            RepositorySnapshot? snapshot;
            try
            {
                snapshot = JsonSerializer.Deserialize<RepositorySnapshot>(File.ReadAllText(meta), Json);
            }
            catch (JsonException)
            {
                continue;
            }

            if (snapshot is { Sha.Length: >= 7 })
            {
                yield return snapshot with { Root = folder };
            }
        }
    }

    private static Task WriteMetadataAsync(RepositorySnapshot snapshot, CancellationToken ct) =>
        File.WriteAllTextAsync(snapshot.Root + ".json", JsonSerializer.Serialize(snapshot with { Root = string.Empty }, Json), ct);

    /// <summary>Drop the repository's other snapshots that are older than <see cref="MaxAge"/> or have no description; a snapshot still being extracted is left alone.</summary>
    private void Purge(string repoFolder, string keep)
    {
        var now = time.GetUtcNow();
        foreach (var folder in System.IO.Directory.EnumerateDirectories(repoFolder).ToList())
        {
            var name = Path.GetFileName(folder);
            if (string.Equals(name, keep, StringComparison.OrdinalIgnoreCase) || name.EndsWith(".extracting", StringComparison.Ordinal))
            {
                continue;
            }

            var meta = folder + ".json";
            var stale = !File.Exists(meta) || System.IO.Directory.GetLastWriteTimeUtc(folder) <= now - MaxAge;
            if (stale)
            {
                System.IO.Directory.Delete(folder, recursive: true);
                File.Delete(meta);
            }
        }
    }

    /// <summary>
    /// The entry's path without the tarball's top folder (GitHub puts the whole tree under one), or null for an entry
    /// outside that folder, under .git, or whose path is not a plain relative one.
    /// </summary>
    internal static string? RelativePath(string entryName, string topFolder)
    {
        var segments = entryName.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2 || segments[0] != topFolder || segments.Any(s => s is "." or ".." || s.Contains(':') || s.Contains('\0')))
        {
            return null;
        }

        var inner = segments[1..];
        return inner[0] == ".git" ? null : string.Join('/', inner);
    }

    private static string? Resolve(RepositorySnapshot snapshot, string relativePath)
    {
        var segments = relativePath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || segments.Any(s => s is "." or ".."))
        {
            return null;
        }

        var full = Path.GetFullPath(Path.Combine(snapshot.Root, Path.Combine(segments)));
        return full.StartsWith(Path.GetFullPath(snapshot.Root) + Path.DirectorySeparatorChar, StringComparison.Ordinal) ? full : null;
    }

    private static bool LooksBinary(byte[] bytes)
    {
        var probe = Math.Min(bytes.Length, 8192);
        for (var i = 0; i < probe; i++)
        {
            if (bytes[i] == 0)
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>The stack record of one repository, as the Docker card reads it.</summary>
internal sealed record StackRecord(
    string Repository,
    string Commit,
    string? Stack,
    string? Image,
    string? Runs,
    IReadOnlyList<DeclaredVersion> Declared,
    string? Refusal,
    DateTimeOffset DetectedAt);

/// <summary>A snapshot Scout refused to take or keep: over the caps, or a tree with a path that climbs out of it.</summary>
internal sealed class SnapshotRefusedException(string message) : Exception(message);
