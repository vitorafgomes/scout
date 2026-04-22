using System.Globalization;
using System.Text;
using Microsoft.Extensions.FileSystemGlobbing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Scout.Plugin.CodeQA.Indexing;

/// <summary>
/// <see cref="IIngestor"/> backed by GitHub's Git Data API via
/// <see cref="IGitHubTreeClient"/>. Produces a filtered, size-capped,
/// deterministically ordered snapshot of the configured repository branch.
/// </summary>
/// <remarks>
/// <para>
/// The ingestor resolves the branch HEAD, pulls the recursive tree, and
/// filters entries in this order: drop non-blobs, drop blobs over
/// <c>Ingestion.MaxFileBytes</c> (so we never download oversized blobs),
/// apply include/exclude globs, then sort by path using
/// <see cref="StringComparison.Ordinal"/>. Remaining blobs are downloaded
/// one by one and skipped if they look binary or fail strict UTF-8
/// decoding.
/// </para>
/// <para>
/// GitHub's <c>trees</c> endpoint silently truncates extremely large trees;
/// the client reports that as <see cref="GitTreeResponse.Truncated"/>. V1
/// treats truncation as a hard error rather than risk ingesting a partial
/// corpus.
/// </para>
/// </remarks>
public sealed class GitHubIngestor : IIngestor
{
    private readonly RepositoryOptions _repository;
    private readonly IngestionOptions _ingestion;
    private readonly IGitHubTreeClient _client;
    private readonly ILogger<GitHubIngestor>? _logger;

    /// <summary>
    /// Internal ctor used by DI. <paramref name="client"/> is the only seam
    /// the tests need; options are bound from configuration upstream.
    /// </summary>
    internal GitHubIngestor(
        IOptions<CodeQAOptions> options,
        IGitHubTreeClient client,
        ILoggerFactory? loggerFactory = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(client);
        if (options.Value is null)
        {
            throw new ArgumentException(
                "CodeQAOptions must be configured.", nameof(options));
        }

        _repository = ValidateRepository(options.Value.Repository);
        _ingestion = ValidateIngestion(options.Value.Ingestion);
        _client = client;
        _logger = loggerFactory?.CreateLogger<GitHubIngestor>();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<RawFile>> IngestAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var head = await _client
            .GetBranchHeadAsync(_repository.Owner, _repository.Name, _repository.Branch, cancellationToken)
            .ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();

        var tree = await _client
            .GetRecursiveTreeAsync(_repository.Owner, _repository.Name, head.CommitSha, cancellationToken)
            .ConfigureAwait(false);

        if (tree.Truncated)
        {
            throw new InvalidOperationException(string.Format(
                CultureInfo.InvariantCulture,
                "GitHub returned a truncated recursive tree for {0}/{1}@{2} (commit {3}). " +
                "V1 refuses to ingest a partial corpus; split the repository or shrink the include globs.",
                _repository.Owner,
                _repository.Name,
                _repository.Branch,
                head.CommitSha));
        }

        var matcher = BuildMatcher(_ingestion);

        var candidates = tree.Items
            .Where(e => e.Type == GitTreeEntryType.Blob)
            .Where(e => e.SizeBytes <= _ingestion.MaxFileBytes)
            .Where(e => matcher.Match(e.Path).HasMatches)
            .OrderBy(e => e.Path, StringComparer.Ordinal)
            .ToArray();

        var results = new List<RawFile>(candidates.Length);
        foreach (var entry in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var bytes = await _client
                .GetBlobRawAsync(_repository.Owner, _repository.Name, entry.Sha, cancellationToken)
                .ConfigureAwait(false);

            if (LooksBinary(bytes))
            {
                _logger?.LogDebug(
                    "Skipping binary blob {Path} (sha={Sha}, size={Size}).",
                    entry.Path,
                    entry.Sha,
                    entry.SizeBytes);
                continue;
            }

            string content;
            try
            {
                content = Utf8Strict.GetString(bytes);
            }
            catch (DecoderFallbackException)
            {
                _logger?.LogDebug(
                    "Skipping non-UTF8 blob {Path} (sha={Sha}, size={Size}).",
                    entry.Path,
                    entry.Sha,
                    entry.SizeBytes);
                continue;
            }

            results.Add(new RawFile(entry.Path, entry.Sha, bytes.Length, content));
        }

        return results.AsReadOnly();
    }

    private static Matcher BuildMatcher(IngestionOptions ingestion)
    {
        // Matcher defaults to StringComparison.OrdinalIgnoreCase; switch to
        // Ordinal so paths that differ only in case are treated as distinct,
        // matching how GitHub stores them.
        var matcher = new Matcher(StringComparison.Ordinal);
        matcher.AddIncludePatterns(ingestion.IncludeGlobs);
        matcher.AddExcludePatterns(ingestion.ExcludeGlobs);
        return matcher;
    }

    private static bool LooksBinary(ReadOnlySpan<byte> bytes)
    {
        // First-8KiB NUL-byte heuristic: git itself uses NUL presence in the
        // prefix as a binary signal. Good enough for source-code ingestion;
        // real binary formats (images, PDFs) hit this in the first block.
        var probe = bytes.Length > 8192 ? bytes[..8192] : bytes;
        for (var i = 0; i < probe.Length; i++)
        {
            if (probe[i] == 0)
            {
                return true;
            }
        }
        return false;
    }

    private static readonly UTF8Encoding Utf8Strict = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    private static RepositoryOptions ValidateRepository(RepositoryOptions repo)
    {
        if (repo is null)
        {
            throw new ArgumentException(
                "CodeQAOptions.Repository must be configured.", nameof(repo));
        }
        if (string.IsNullOrWhiteSpace(repo.Owner))
        {
            throw new ArgumentException(
                "RepositoryOptions.Owner must be non-empty.", nameof(repo));
        }
        if (string.IsNullOrWhiteSpace(repo.Name))
        {
            throw new ArgumentException(
                "RepositoryOptions.Name must be non-empty.", nameof(repo));
        }
        if (string.IsNullOrWhiteSpace(repo.Branch))
        {
            throw new ArgumentException(
                "RepositoryOptions.Branch must be non-empty.", nameof(repo));
        }
        if (string.IsNullOrWhiteSpace(repo.TokenEnvVar))
        {
            throw new ArgumentException(
                "RepositoryOptions.TokenEnvVar must be non-empty.", nameof(repo));
        }
        return repo;
    }

    private static IngestionOptions ValidateIngestion(IngestionOptions ingestion)
    {
        if (ingestion is null)
        {
            throw new ArgumentException(
                "CodeQAOptions.Ingestion must be configured.", nameof(ingestion));
        }
        if (ingestion.IncludeGlobs is null || ingestion.IncludeGlobs.Count == 0)
        {
            throw new ArgumentException(
                "IngestionOptions.IncludeGlobs must contain at least one pattern.", nameof(ingestion));
        }
        if (ingestion.ExcludeGlobs is null)
        {
            throw new ArgumentException(
                "IngestionOptions.ExcludeGlobs must not be null.", nameof(ingestion));
        }
        if (ingestion.MaxFileBytes < 1)
        {
            throw new ArgumentException(
                "IngestionOptions.MaxFileBytes must be >= 1.", nameof(ingestion));
        }
        return ingestion;
    }
}
