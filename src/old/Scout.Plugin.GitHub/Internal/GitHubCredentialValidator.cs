using System.Globalization;
using System.Net;
using Microsoft.Extensions.Options;
using Octokit;
using Scout.Core.Features;

namespace Scout.Plugin.GitHub.Internal;

/// <summary>
/// Validates a PAT against the GitHub API without persisting it. The
/// validator probes <c>/user</c>, then inspects the response's OAuth
/// scope header to confirm the <c>repo</c> scope is present.
/// </summary>
public sealed class GitHubCredentialValidator : IFeatureCredentialValidator
{
    private const string PatField = "pat";
    private const string RequiredScope = "repo";

    private readonly IOptions<GitHubPluginOptions> _options;
    private readonly Func<string, IGitHubClient> _clientFactory;

    /// <summary>Production constructor.</summary>
    public GitHubCredentialValidator(IOptions<GitHubPluginOptions> options)
        : this(options, clientOverride: null)
    {
    }

    /// <summary>Test seam that injects a substitute GitHub client.</summary>
    internal GitHubCredentialValidator(
        IOptions<GitHubPluginOptions> options,
        Func<string, IGitHubClient>? clientOverride)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _clientFactory = clientOverride ?? DefaultClient;
    }

    /// <inheritdoc/>
    public string FeatureId => "github";

    /// <inheritdoc/>
    public async Task<FeatureCredentialValidationResult> ValidateAsync(
        IReadOnlyDictionary<string, string> credentialBody,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(credentialBody);
        cancellationToken.ThrowIfCancellationRequested();

        if (!credentialBody.TryGetValue(PatField, out var pat) || string.IsNullOrWhiteSpace(pat))
        {
            return Invalid("PAT is required.");
        }

        var client = _clientFactory(pat);

        try
        {
            var me = await client.User.Current().ConfigureAwait(false);

            var apiInfo = client.GetLastApiInfo();
            var scopes = apiInfo?.OauthScopes;
            if (scopes is null || !scopes.Any(s => string.Equals(s, RequiredScope, StringComparison.OrdinalIgnoreCase)))
            {
                return MissingScopes("Token missing required scope: repo.");
            }

            var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["login"] = me.Login,
            };
            return new FeatureCredentialValidationResult(
                FeatureCredentialValidationOutcome.Valid,
                string.Format(CultureInfo.InvariantCulture, "Authenticated as {0}.", me.Login),
                metadata);
        }
        catch (RateLimitExceededException ex)
        {
            return new FeatureCredentialValidationResult(
                FeatureCredentialValidationOutcome.RateLimited,
                string.Format(CultureInfo.InvariantCulture, "Rate limit hit; resets at {0:O}.", ex.Reset));
        }
        catch (AuthorizationException)
        {
            return Invalid("PAT rejected by GitHub.");
        }
        catch (ForbiddenException)
        {
            return MissingScopes("GitHub returned 403; check token scopes.");
        }
        catch (ApiException ex) when (
            ex.StatusCode == HttpStatusCode.ServiceUnavailable
            || ex.StatusCode == HttpStatusCode.GatewayTimeout)
        {
            return new FeatureCredentialValidationResult(
                FeatureCredentialValidationOutcome.TransientError,
                ex.Message);
        }
    }

    private IGitHubClient DefaultClient(string pat)
    {
        return new GitHubClient(new ProductHeaderValue(_options.Value.UserAgent))
        {
            Credentials = new Credentials(pat),
        };
    }

    private static FeatureCredentialValidationResult Invalid(string detail) =>
        new(FeatureCredentialValidationOutcome.Invalid, detail);

    private static FeatureCredentialValidationResult MissingScopes(string detail) =>
        new(FeatureCredentialValidationOutcome.MissingScopes, detail);
}
