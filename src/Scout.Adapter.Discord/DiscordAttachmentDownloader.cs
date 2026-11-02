namespace Scout.Adapter.Discord;

/// <summary>
/// Downloads a Discord attachment (a voice message) from the URL Discord put on
/// the message. A read only: one HTTPS GET.
/// </summary>
internal static class DiscordAttachmentDownloader
{
    internal static readonly HttpClient Shared = new(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(2) })
    {
        Timeout = TimeSpan.FromSeconds(30),
    };

    /// <summary>Opens the attachment body; a non-success status throws <see cref="HttpRequestException"/>.</summary>
    public static async Task<Stream> OpenAsync(HttpClient client, string url, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new ArgumentException("Attachment URL must be an absolute https URL.", nameof(url));
        }

        var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }
}
