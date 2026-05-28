using System.Net;
using Microsoft.Bot.Schema;

namespace Scout.Adapter.Teams.Streaming;

/// <summary>
/// Maps the Bot Framework SDK's <see cref="ErrorResponseException"/> (and any
/// inner HTTP status it carries) into the transport-neutral
/// <see cref="TeamsTransportException"/> the sink reacts to. Keeping this mapping
/// in one place means the sink — and its tests — depend only on a status code and
/// a Teams error code, never on the SDK's exception surface.
/// </summary>
internal static class TeamsErrorMapper
{
    public static bool TryMap(Exception exception, out TeamsTransportException mapped)
    {
        if (exception is ErrorResponseException ere)
        {
            var status = ere.Response is { } resp ? (int)resp.StatusCode : 0;
            var code = ere.Body?.Error?.Code;
            mapped = new TeamsTransportException(status, code, ere.Message, ere);
            return true;
        }

        // A bare HttpRequestException may carry a status code on newer runtimes.
        if (exception is HttpRequestException { StatusCode: { } httpStatus } hre)
        {
            mapped = new TeamsTransportException((int)httpStatus, null, hre.Message, hre);
            return true;
        }

        mapped = new TeamsTransportException((int)HttpStatusCode.InternalServerError);
        return false;
    }
}
