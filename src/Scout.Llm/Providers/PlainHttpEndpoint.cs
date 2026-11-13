using System.Net;
using Microsoft.Extensions.Logging;

namespace Scout.Llm.Providers;

internal static class PlainHttpEndpoint
{
    internal static void WarnIfKeyLeavesInClear(ILogger? logger, string provider, Uri endpoint)
    {
        if (logger is null || endpoint.Scheme != Uri.UriSchemeHttp || IsThisMachine(endpoint))
        {
            return;
        }

        logger.LogWarning(
            "The {Provider} provider sends its API key over plain {Scheme} to {Host}, which is not this machine; " +
            "an https endpoint keeps the key encrypted.",
            provider, endpoint.Scheme, endpoint.Host);
    }

    internal static string Shown(string? endpoint) =>
        !Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.Host.Length == 0 ? "unknown"
            : uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps ? uri.GetComponents(UriComponents.SchemeAndServer, UriFormat.UriEscaped)
            : uri.GetComponents(UriComponents.SchemeAndServer, UriFormat.UriEscaped) + (uri.AbsolutePath == "/" ? string.Empty : uri.AbsolutePath);

    private static bool IsThisMachine(Uri endpoint)
        => string.Equals(endpoint.Host, "localhost", StringComparison.OrdinalIgnoreCase)
            || (IPAddress.TryParse(endpoint.DnsSafeHost, out var address) && IPAddress.IsLoopback(address));
}
