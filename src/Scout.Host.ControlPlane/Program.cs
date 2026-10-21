using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Scout.Host.ControlPlane;
using Scout.Host.ControlPlane.Transport;

var builder = WebApplication.CreateBuilder(args);

// gRPC + token interceptor + domain services. Token is read from
// SCOUT_CONTROLPLANE_TOKEN or minted at startup.
builder.Services.AddControlPlaneHost(builder.Configuration);

// Windows named-pipe ACL: restrict to the current user.
if (ControlPlaneEndpoint.UseNamedPipe && OperatingSystem.IsWindows())
{
    NamedPipeSecurity.RestrictToCurrentUser(builder.WebHost);
}

string? socketPath = null;
builder.WebHost.ConfigureKestrel(kestrel =>
{
    socketPath = KestrelTransportConfigurator.Configure(
        kestrel,
        socketPathOverride: builder.Configuration["Scout:ControlPlane:SocketPath"],
        pipeName: ControlPlaneEndpoint.DefaultPipeName);
});

var app = builder.Build();
app.MapControlPlaneServices();

// Fail loud if the native stat the UDS anti-hijack pre-flight depends on
// cannot be trusted on this platform (unresolved symbol, unsupported arch,
// mis-ported struct offsets). Runs before Kestrel binds in RunAsync.
if (!OperatingSystem.IsWindows() && !NativeUnix.SelfTest())
{
    app.Logger.LogCritical(
        "UDS ownership self-test failed: native stat is unreliable on this platform. Refusing to start.");
    throw new InvalidOperationException(
        "Control plane cannot verify Unix socket ownership on this platform; refusing to start.");
}

// Once Kestrel has created the socket, log the bind line and tighten it to
// 0600. Named pipes are ACL'd at creation time, so the chmod only applies to
// the UDS path. Logging uses the host's configured logger, not a throwaway.
if (socketPath is not null && !OperatingSystem.IsWindows())
{
    var resolvedPath = socketPath;
    app.Lifetime.ApplicationStarted.Register(() =>
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        app.Logger.LogInformation("Control plane listening on UDS {SocketPath} (h2c)", resolvedPath);
        try
        {
            ControlPlaneEndpoint.HardenSocket(resolvedPath);
        }
        catch (Exception ex)
        {
            app.Logger.LogWarning(ex, "Failed to harden socket permissions for {SocketPath}", resolvedPath);
        }
    });
}

await app.RunAsync();
