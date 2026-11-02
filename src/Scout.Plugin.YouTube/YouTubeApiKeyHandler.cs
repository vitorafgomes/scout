namespace Scout.Plugin.YouTube;

/// <summary>
/// Appends the configured Data API key to the query string of every YouTube request. The key
/// travels in the URL only, never in a header, and no message this plugin produces carries
/// the request URL.
/// </summary>
internal sealed class YouTubeApiKeyHandler(string apiKey) : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri!;
        var separator = string.IsNullOrEmpty(uri.Query) ? "?" : "&";
        request.RequestUri = new Uri(uri.AbsoluteUri + separator + "key=" + Uri.EscapeDataString(apiKey));
        return base.SendAsync(request, cancellationToken);
    }
}
