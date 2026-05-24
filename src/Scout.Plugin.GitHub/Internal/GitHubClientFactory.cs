using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Octokit;
using Scout.Core.Features;
using ICredentialStore = Scout.Core.Features.ICredentialStore;

namespace Scout.Plugin.GitHub.Internal;

/// <summary>
/// Default <see cref="IGitHubClientFactory"/>. Looks up the PAT in the
/// credential store and caches the resulting <see cref="IGitHubClient"/>
/// for a short TTL keyed by a SHA256 hash of the token so that PAT
/// rotation invalidates the cache without comparing the secret itself.
/// </summary>
public sealed class GitHubClientFactory : IGitHubClientFactory
{
    internal static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);

    private readonly ICredentialStore _credentialStore;
    private readonly IOptions<GitHubPluginOptions> _options;
    private readonly ILogger<GitHubClientFactory> _logger;
    private readonly Func<string, IGitHubClient> _clientFactory;
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new(StringComparer.Ordinal);

    /// <summary>Production constructor.</summary>
    public GitHubClientFactory(
        ICredentialStore credentialStore,
        IOptions<GitHubPluginOptions> options,
        ILogger<GitHubClientFactory> logger)
        : this(credentialStore, options, logger, clientOverride: null)
    {
    }

    /// <summary>
    /// Test seam. The <paramref name="clientOverride"/> is invoked with
    /// the PAT instead of building a real <see cref="GitHubClient"/>.
    /// </summary>
    internal GitHubClientFactory(
        ICredentialStore credentialStore,
        IOptions<GitHubPluginOptions> options,
        ILogger<GitHubClientFactory> logger,
        Func<string, IGitHubClient>? clientOverride)
    {
        _credentialStore = credentialStore ?? throw new ArgumentNullException(nameof(credentialStore));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _clientFactory = clientOverride ?? DefaultClient;
    }

    /// <inheritdoc/>
    public async Task<IGitHubClient> CreateAsync(CancellationToken cancellationToken)
    {
        var opts = _options.Value
            ?? throw new InvalidOperationException("GitHubPluginOptions must be configured.");

        var pat = await _credentialStore
            .GetAsync(opts.CredentialKey, cancellationToken)
            .ConfigureAwait(false);

        if (string.IsNullOrEmpty(pat))
        {
            throw new InvalidOperationException(
                "GitHub plugin enabled but PAT credential missing");
        }

        var hash = Hash(pat);
        var now = DateTime.UtcNow;

        if (_cache.TryGetValue(hash, out var entry)
            && now - entry.FetchedAt < CacheTtl)
        {
            return entry.Client;
        }

        // PAT rotation: drop every cached client. Each instance keeps only
        // a single hash, but a previous PAT may still live until eviction.
        _cache.Clear();

        var client = _clientFactory(pat);
        _cache[hash] = new CacheEntry(client, now);

        _logger.LogDebug("GitHub client built (cache miss; ttl={Ttl}).", CacheTtl);
        return client;
    }

    private IGitHubClient DefaultClient(string pat)
    {
        return new GitHubClient(new ProductHeaderValue(_options.Value.UserAgent))
        {
            Credentials = new Credentials(pat),
        };
    }

    private static string Hash(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        var digest = SHA256.HashData(bytes);
        return Convert.ToHexString(digest);
    }

    private readonly record struct CacheEntry(IGitHubClient Client, DateTime FetchedAt);
}
