using System.Globalization;
using Microsoft.Extensions.Options;
using Octokit;

namespace Scout.Plugin.CodeQA.Indexing;

/// <summary>
/// <see cref="IGitHubTreeClient"/> backed by Octokit. Owns credential
/// acquisition (reads the configured env var), exception translation, and
/// the mapping from Octokit's rich DTOs to the narrow records the ingestor
/// consumes.
/// </summary>
/// <remarks>
/// Lives in the CodeQA plugin for V1 since it is the only consumer. Extract
/// to a dedicated <c>Scout.SourceHost.GitHub</c> project when a second
/// source host (GitLab/Bitbucket) is added.
/// </remarks>
internal sealed class OctokitGitHubTreeClient : IGitHubTreeClient
{
    private readonly IGitHubClient _client;
    private readonly RepositoryOptions _repository;

    /// <summary>
    /// Production ctor. Resolves the PAT from the environment variable
    /// named in <see cref="RepositoryOptions.TokenEnvVar"/> and builds an
    /// authenticated <see cref="GitHubClient"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The env var is missing or empty.
    /// </exception>
    public OctokitGitHubTreeClient(IOptions<CodeQAOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Value is null)
        {
            throw new ArgumentException(
                "CodeQAOptions must be configured.", nameof(options));
        }

        var repository = options.Value.Repository
            ?? throw new ArgumentException(
                "CodeQAOptions.Repository must be configured.", nameof(options));

        if (string.IsNullOrWhiteSpace(repository.TokenEnvVar))
        {
            throw new ArgumentException(
                "RepositoryOptions.TokenEnvVar must be non-empty.", nameof(options));
        }

        var pat = Environment.GetEnvironmentVariable(repository.TokenEnvVar);
        if (string.IsNullOrEmpty(pat))
        {
            throw new InvalidOperationException(string.Format(
                CultureInfo.InvariantCulture,
                "GitHub PAT not found. Set the {0} environment variable to a token with repo:read scope.",
                repository.TokenEnvVar));
        }

        _client = new GitHubClient(new ProductHeaderValue("Scout"))
        {
            Credentials = new Credentials(pat),
        };
        _repository = repository;
    }

    /// <summary>
    /// Test seam. Accepts a pre-built Octokit client so tests can mock the
    /// boundary and a repository snapshot without going through the env
    /// variable.
    /// </summary>
    internal OctokitGitHubTreeClient(IGitHubClient client, RepositoryOptions repository)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(repository);
        _client = client;
        _repository = repository;
    }

    public async Task<GitReference> GetBranchHeadAsync(
        string owner,
        string repo,
        string branch,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return await TranslateAsync(
            async () =>
            {
                var result = await _client.Repository.Branch.Get(owner, repo, branch).ConfigureAwait(false);
                return new GitReference(result.Commit.Sha);
            },
            _repository).ConfigureAwait(false);
    }

    public async Task<GitTreeResponse> GetRecursiveTreeAsync(
        string owner,
        string repo,
        string commitSha,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return await TranslateAsync(
            async () =>
            {
                var response = await _client.Git.Tree.GetRecursive(owner, repo, commitSha).ConfigureAwait(false);
                var items = new List<GitTreeEntry>(response.Tree.Count);
                foreach (var item in response.Tree)
                {
                    items.Add(new GitTreeEntry(
                        Path: item.Path,
                        Sha: item.Sha,
                        SizeBytes: item.Size,
                        Type: MapTreeType(item.Type.Value)));
                }
                return new GitTreeResponse(items, response.Truncated);
            },
            _repository).ConfigureAwait(false);
    }

    public async Task<byte[]> GetBlobRawAsync(
        string owner,
        string repo,
        string blobSha,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return await TranslateAsync(
            async () =>
            {
                var blob = await _client.Git.Blob.Get(owner, repo, blobSha).ConfigureAwait(false);
                return DecodeBlob(blob);
            },
            _repository).ConfigureAwait(false);
    }

    private static byte[] DecodeBlob(Blob blob)
    {
        // GitHub returns blob content base64-encoded by default. The Utf8
        // encoding branch is defensive: Octokit exposes it, and a
        // conformant server may elect to return decoded text for small
        // blobs.
        return blob.Encoding.Value switch
        {
            EncodingType.Base64 => Convert.FromBase64String(blob.Content),
            EncodingType.Utf8 => System.Text.Encoding.UTF8.GetBytes(blob.Content),
            _ => throw new InvalidOperationException(string.Format(
                CultureInfo.InvariantCulture,
                "GitHub returned a blob with unsupported encoding '{0}' (sha={1}).",
                blob.Encoding.StringValue,
                blob.Sha)),
        };
    }

    private static GitTreeEntryType MapTreeType(TreeType type) => type switch
    {
        TreeType.Blob => GitTreeEntryType.Blob,
        TreeType.Tree => GitTreeEntryType.Tree,
        TreeType.Commit => GitTreeEntryType.Commit,
        _ => throw new InvalidOperationException(string.Format(
            CultureInfo.InvariantCulture,
            "Unknown GitHub tree entry type '{0}'.",
            type)),
    };

    internal static async Task<T> TranslateAsync<T>(Func<Task<T>> call, RepositoryOptions repository)
    {
        ArgumentNullException.ThrowIfNull(call);
        ArgumentNullException.ThrowIfNull(repository);
        try
        {
            return await call().ConfigureAwait(false);
        }
        catch (AuthorizationException ex)
        {
            throw new InvalidOperationException(string.Format(
                CultureInfo.InvariantCulture,
                "GitHub PAT missing or lacks repo:read scope (env var {0}).",
                repository.TokenEnvVar), ex);
        }
        catch (NotFoundException ex)
        {
            throw new InvalidOperationException(string.Format(
                CultureInfo.InvariantCulture,
                "GitHub repository or branch not found: {0}/{1}@{2}.",
                repository.Owner,
                repository.Name,
                repository.Branch), ex);
        }
        catch (RateLimitExceededException ex)
        {
            throw new InvalidOperationException(string.Format(
                CultureInfo.InvariantCulture,
                "GitHub rate limit exhausted; resets at {0:O}. Authenticated PAT limit is 5000/hr.",
                ex.Reset), ex);
        }
    }
}
