using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace Scout.Host.Worker.Http;

/// <summary>
/// Defence-in-depth filter that rejects any request whose remote address
/// is not a loopback. Kestrel is already bound to 127.0.0.1, but a
/// misconfiguration upstream (reverse proxy, kernel route, container
/// port-publish) must not silently expose the worker to the network.
/// </summary>
public sealed class LoopbackOnlyMiddleware
{
    private static readonly byte[] ForbiddenBody = JsonSerializer.SerializeToUtf8Bytes(
        new
        {
            code = "providers/forbidden-origin",
            message = "Only loopback connections are allowed",
        });

    private readonly RequestDelegate _next;

    public LoopbackOnlyMiddleware(RequestDelegate next)
    {
        _next = next ?? throw new ArgumentNullException(nameof(next));
    }

    public async Task InvokeAsync(HttpContext context)
    {
        IPAddress? remote = context.Connection.RemoteIpAddress;

        if (remote is null || !IPAddress.IsLoopback(remote))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            context.Response.ContentType = "application/json";
            await context.Response.Body.WriteAsync(ForbiddenBody).ConfigureAwait(false);
            return;
        }

        await _next(context).ConfigureAwait(false);
    }
}
