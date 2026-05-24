using Octokit;

namespace Scout.Plugin.GitHub.Internal;

/// <summary>
/// Resolves an authenticated <see cref="IGitHubClient"/> from the PAT
/// stored in <see cref="Scout.Core.Features.ICredentialStore"/>.
/// Implementations are expected to cache within a short TTL so each
/// kernel function call does not re-hit the credential store.
/// </summary>
public interface IGitHubClientFactory
{
    /// <summary>
    /// Build (or return a cached) GitHub client. Throws when the PAT is
    /// not present in the credential store.
    /// </summary>
    Task<IGitHubClient> CreateAsync(CancellationToken cancellationToken);
}
