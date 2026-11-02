using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Scout.Plugin.YouTube;

/// <summary>
/// Sets the Bearer header of every YouTube request from the token file the desktop writes
/// after the owner signs in with Google, re-reading it whenever its write time changes, so a
/// refreshed token is used without a restart. A missing, empty or unreadable file fails the
/// call before it leaves the process, and a 401 from YouTube asks the owner to sign in again;
/// neither message carries the token or the file contents.
/// </summary>
internal sealed class YouTubeTokenFileHandler(string tokenFile) : DelegatingHandler
{
    internal const string NotConnectedMessage =
        "YouTube is not connected. Sign in from the Scout app.";

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
        throw new YouTubeApiException(YouTubeClient.SignInAgainMessage, HttpStatusCode.Unauthorized);
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

        throw new YouTubeApiException(NotConnectedMessage);
    }

    private sealed record CachedToken(string AccessToken, DateTime WrittenAtUtc);
}
