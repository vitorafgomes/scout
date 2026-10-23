using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Scout.Plugin.GitHub;

/// <summary>
/// Sets the Bearer header of every GitHub request from the token file the desktop
/// writes after "Connect with GitHub", re-reading it whenever its write time changes,
/// so a refreshed token is used without a restart. A missing, empty or unreadable file
/// fails the call before it leaves the process, and a 401 from GitHub asks the owner to
/// connect again; neither message carries the token or the file contents.
/// </summary>
internal sealed class GitHubTokenFileHandler(string tokenFile) : DelegatingHandler
{
    internal const string NotConnectedMessage =
        "GitHub is not connected. Connect it on the Features screen.";

    internal const string RejectedMessage =
        "GitHub rejected the connection. Disconnect and connect GitHub again on the Features screen.";

    private volatile CachedToken? _cached;

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", CurrentToken());
        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.Unauthorized)
        {
            return response;
        }

        response.Dispose();
        throw new GitHubApiException(RejectedMessage, HttpStatusCode.Unauthorized);
    }

    private string CurrentToken()
    {
        var writtenAt = File.GetLastWriteTimeUtc(tokenFile);
        var cached = _cached;
        if (cached is not null && cached.WrittenAtUtc == writtenAt)
        {
            return cached.AccessToken;
        }

        var fresh = new CachedToken(Read(), writtenAt);
        _cached = fresh;
        return fresh.AccessToken;
    }

    private string Read()
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(tokenFile));
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("access_token", out var token)
                && token.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(token.GetString()))
            {
                return token.GetString()!;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
        }

        throw new GitHubApiException(NotConnectedMessage);
    }

    private sealed record CachedToken(string AccessToken, DateTime WrittenAtUtc);
}
