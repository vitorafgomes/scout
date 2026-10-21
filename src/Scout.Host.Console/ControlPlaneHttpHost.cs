using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Scout.ControlPlane.Features;
using Scout.ControlPlane.Providers;

namespace Scout.Host.Console;

/// <summary>
/// Serves the provider and feature control surface over a loopback HTTP
/// endpoint the desktop UI drives. Bind happens in <see cref="StartAsync"/> so
/// the assigned port is announced on stdout before the host reports started.
/// Uses the framework <see cref="HttpListener"/> to keep the worker off the web
/// stack and its bundle small.
/// </summary>
internal sealed class ControlPlaneHttpHost : IHostedService
{
    private const string HttpMarkerPrefix = "SCOUT_HTTP=127.0.0.1:";
    private const string TokenEnvVarName = "SCOUT_CONTROLPLANE_TOKEN";
    private const string TokenHeaderName = "x-scout-token";
    private const string LoopbackAddress = "127.0.0.1";
    private const long MaxRequestBodyBytes = 256 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly ProvidersService _providers;
    private readonly FeaturesService _features;
    private readonly ILogger<ControlPlaneHttpHost> _logger;
    private readonly CancellationTokenSource _cts = new();

    private HttpListener? _listener;
    private Task? _acceptLoop;
    private string? _expectedToken;

    public ControlPlaneHttpHost(
        ProvidersService providers,
        FeaturesService features,
        ILogger<ControlPlaneHttpHost> logger)
    {
        _providers = providers ?? throw new ArgumentNullException(nameof(providers));
        _features = features ?? throw new ArgumentNullException(nameof(features));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>The loopback port the listener bound to. Zero until started.</summary>
    internal int Port { get; private set; }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _expectedToken = Environment.GetEnvironmentVariable(TokenEnvVarName);

        // Fail closed: without the shared secret the loopback surface would be
        // reachable unauthenticated by any local process, so refuse to serve at
        // all rather than bind an open control surface.
        if (string.IsNullOrEmpty(_expectedToken))
        {
            _logger.LogError(
                "Control-plane HTTP surface disabled: {EnvVar} is not set.",
                TokenEnvVarName);
            return Task.CompletedTask;
        }

        var port = ReserveLoopbackPort();

        var listener = new HttpListener();
        listener.Prefixes.Add($"http://{LoopbackAddress}:{port}/");
        listener.Start();

        _listener = listener;
        Port = port;

        // Announce the endpoint on stdout exactly like the readiness marker so
        // the supervisor's line parser can discover it. Kept off the logging
        // pipeline so the parse sees a clean, unformatted line.
        System.Console.Out.WriteLine($"{HttpMarkerPrefix}{port}");
        System.Console.Out.Flush();

        _acceptLoop = Task.Run(() => AcceptLoopAsync(_cts.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _cts.CancelAsync().ConfigureAwait(false);

        try
        {
            _listener?.Stop();
            _listener?.Close();
        }
        catch (ObjectDisposedException)
        {
            // Already torn down; nothing to do.
        }

        if (_acceptLoop is not null)
        {
            try
            {
                await _acceptLoop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected while the loop unwinds on shutdown.
            }
        }

        // Not disposing the CTS: in-flight request tasks spawned by the accept
        // loop still hold its token, and the process is exiting on stop anyway.
    }

    // Bind a throwaway socket to port 0 to let the OS pick a free loopback port,
    // then hand that number to the listener. The tiny window between release and
    // rebind is acceptable: the surface is loopback-only.
    private static int ReserveLoopbackPort()
    {
        var probe = new TcpListener(new IPEndPoint(IPAddress.Loopback, 0));
        probe.Start();
        try
        {
            return ((IPEndPoint)probe.LocalEndpoint).Port;
        }
        finally
        {
            probe.Stop();
        }
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        var listener = _listener!;
        while (!ct.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (
                ex is HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
                // Expected when the listener is stopped or disposed during shutdown.
                break;
            }
            catch (Exception ex)
            {
                // Anything else is unexpected; surface it instead of dying silently.
                _logger.LogError(ex, "Control-plane accept loop terminated unexpectedly.");
                break;
            }

            _ = Task.Run(() => HandleContextSafeAsync(context, ct), CancellationToken.None);
        }
    }

    private async Task HandleContextSafeAsync(HttpListenerContext context, CancellationToken ct)
    {
        try
        {
            await HandleContextAsync(context, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutting down mid-request; drop it quietly.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled error serving control-plane request.");
            try
            {
                await WriteErrorAsync(context.Response, 500, "providers/request-failed", "Internal error.", ct)
                    .ConfigureAwait(false);
            }
            catch (Exception writeEx)
            {
                _logger.LogDebug(writeEx, "Failed to write control-plane error response.");
            }
        }
        finally
        {
            try
            {
                context.Response.Close();
            }
            catch (Exception closeEx)
            {
                _logger.LogDebug(closeEx, "Failed to close control-plane response.");
            }
        }
    }

    private async Task HandleContextAsync(HttpListenerContext context, CancellationToken ct)
    {
        var request = context.Request;
        var response = context.Response;

        if (!string.IsNullOrEmpty(_expectedToken))
        {
            var presented = request.Headers[TokenHeaderName];
            if (!TokenMatches(_expectedToken, presented))
            {
                await WriteErrorAsync(response, 401, "providers/request-failed", "unauthorized", ct)
                    .ConfigureAwait(false);
                return;
            }
        }

        if (request.ContentLength64 > MaxRequestBodyBytes)
        {
            await WriteErrorAsync(response, 413, "providers/request-failed", "Request body too large.", ct)
                .ConfigureAwait(false);
            return;
        }

        try
        {
            await RouteAsync(request, response, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            var (status, code, message) = ControlPlaneErrorMapper.Map(ex);
            await WriteErrorAsync(response, status, code, message, ct).ConfigureAwait(false);
        }
    }

    private async Task RouteAsync(HttpListenerRequest request, HttpListenerResponse response, CancellationToken ct)
    {
        var method = request.HttpMethod;
        var segments = (request.Url?.AbsolutePath ?? "/")
            .Split('/', StringSplitOptions.RemoveEmptyEntries);

        var handled = segments switch
        {
            [ "providers", .. ] => await RouteProvidersAsync(method, segments, request, response, ct).ConfigureAwait(false),
            [ "features", .. ] => await RouteFeaturesAsync(method, segments, request, response, ct).ConfigureAwait(false),
            _ => false,
        };

        if (!handled)
        {
            await WriteErrorAsync(response, 404, "providers/request-failed", "Not found.", ct).ConfigureAwait(false);
        }
    }

    private async Task<bool> RouteProvidersAsync(
        string method,
        string[] segments,
        HttpListenerRequest request,
        HttpListenerResponse response,
        CancellationToken ct)
    {
        if (segments is [ "providers" ] && method == HttpMethod.Get.Method)
        {
            var statuses = await _providers.ListAsync(ct).ConfigureAwait(false);
            var dto = statuses.Select(s => ControlPlaneHttpContracts.ToDto(s)).ToArray();
            await WriteJsonAsync(response, 200, dto, ct).ConfigureAwait(false);
            return true;
        }

        if (segments is not [ "providers", var idSegment, var action ])
        {
            return false;
        }

        var id = Uri.UnescapeDataString(idSegment);

        switch (action)
        {
            case "status" when method == HttpMethod.Get.Method:
                await WriteStatusAsync(response, await _providers.GetStatusAsync(id, ct).ConfigureAwait(false), ct)
                    .ConfigureAwait(false);
                return true;
            case "start" when method == HttpMethod.Post.Method:
                await WriteStatusAsync(response, await _providers.StartAsync(id, ct).ConfigureAwait(false), ct)
                    .ConfigureAwait(false);
                return true;
            case "stop" when method == HttpMethod.Post.Method:
                await WriteStatusAsync(response, await _providers.StopAsync(id, ct).ConfigureAwait(false), ct)
                    .ConfigureAwait(false);
                return true;
            case "restart" when method == HttpMethod.Post.Method:
                await WriteStatusAsync(response, await _providers.RestartAsync(id, ct).ConfigureAwait(false), ct)
                    .ConfigureAwait(false);
                return true;
            case "autostart" when method == HttpMethod.Put.Method:
                var body = await ReadJsonAsync<AutostartRequestDto>(request, ct).ConfigureAwait(false)
                    ?? throw new ArgumentException("Missing autostart request body.");
                await _providers.SetAutostartAsync(id, body.Enabled, ct).ConfigureAwait(false);
                WriteNoContent(response);
                return true;
            default:
                return false;
        }
    }

    private async Task<bool> RouteFeaturesAsync(
        string method,
        string[] segments,
        HttpListenerRequest request,
        HttpListenerResponse response,
        CancellationToken ct)
    {
        if (segments is [ "features" ] && method == HttpMethod.Get.Method)
        {
            var snapshots = await _features.ListAsync(ct).ConfigureAwait(false);
            var dto = snapshots.Select(s => ControlPlaneHttpContracts.ToDto(s)).ToArray();
            await WriteJsonAsync(response, 200, dto, ct).ConfigureAwait(false);
            return true;
        }

        if (segments is not [ "features", var idSegment, var action ])
        {
            return false;
        }

        var id = Uri.UnescapeDataString(idSegment);

        switch (action)
        {
            case "credentials" when method == HttpMethod.Post.Method:
                var credentials = await ReadJsonAsync<Dictionary<string, string>>(request, ct).ConfigureAwait(false)
                    ?? new Dictionary<string, string>(StringComparer.Ordinal);
                await WriteFeatureAsync(
                    response,
                    await _features.SetCredentialsAsync(id, credentials, ct).ConfigureAwait(false),
                    ct).ConfigureAwait(false);
                return true;
            case "credentials" when method == HttpMethod.Delete.Method:
                await WriteFeatureAsync(
                    response,
                    await _features.DeleteCredentialsAsync(id, ct).ConfigureAwait(false),
                    ct).ConfigureAwait(false);
                return true;
            case "setup" when method == HttpMethod.Post.Method:
                var setup = await ReadJsonAsync<SetupRequestDto>(request, ct).ConfigureAwait(false)
                    ?? new SetupRequestDto(null, null);
                var input = new FeatureSetupInput(
                    Credentials: setup.Credentials is { Count: > 0 } ? setup.Credentials : null,
                    Settings: setup.Settings is { Count: > 0 } ? setup.Settings : null);
                await WriteFeatureAsync(
                    response,
                    await _features.SetupAsync(id, input, ct).ConfigureAwait(false),
                    ct).ConfigureAwait(false);
                return true;
            case "enable" when method == HttpMethod.Post.Method:
                await WriteFeatureAsync(
                    response,
                    await _features.EnableAsync(id, ct).ConfigureAwait(false),
                    ct).ConfigureAwait(false);
                return true;
            case "disable" when method == HttpMethod.Post.Method:
                await WriteFeatureAsync(
                    response,
                    await _features.DisableAsync(id, ct).ConfigureAwait(false),
                    ct).ConfigureAwait(false);
                return true;
            default:
                return false;
        }
    }

    private static bool TokenMatches(string expected, string? presented)
    {
        if (string.IsNullOrEmpty(presented))
        {
            return false;
        }

        var expectedBytes = Encoding.UTF8.GetBytes(expected);
        var presentedBytes = Encoding.UTF8.GetBytes(presented);
        return CryptographicOperations.FixedTimeEquals(presentedBytes, expectedBytes);
    }

    private Task WriteStatusAsync(HttpListenerResponse response, ProviderStatus status, CancellationToken ct) =>
        WriteJsonAsync(response, 200, ControlPlaneHttpContracts.ToDto(status), ct);

    private Task WriteFeatureAsync(HttpListenerResponse response, FeatureSnapshot snapshot, CancellationToken ct) =>
        WriteJsonAsync(response, 200, ControlPlaneHttpContracts.ToDto(snapshot), ct);

    private static async Task<T?> ReadJsonAsync<T>(HttpListenerRequest request, CancellationToken ct)
    {
        if (!request.HasEntityBody)
        {
            return default;
        }

        return await JsonSerializer.DeserializeAsync<T>(request.InputStream, JsonOptions, ct).ConfigureAwait(false);
    }

    private static async Task WriteJsonAsync<T>(
        HttpListenerResponse response,
        int status,
        T payload,
        CancellationToken ct)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions);
        response.StatusCode = status;
        response.ContentType = "application/json";
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes, ct).ConfigureAwait(false);
    }

    private static Task WriteErrorAsync(
        HttpListenerResponse response,
        int status,
        string code,
        string message,
        CancellationToken ct) =>
        WriteJsonAsync(response, status, new ErrorDto(code, message), ct);

    private static void WriteNoContent(HttpListenerResponse response)
    {
        response.StatusCode = 204;
        response.ContentLength64 = 0;
    }
}
