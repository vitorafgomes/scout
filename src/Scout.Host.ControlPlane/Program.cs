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
    var logger = LoggerFactory
        .Create(b => b.AddConsole())
        .CreateLogger("Scout.Host.ControlPlane.Transport");

    socketPath = KestrelTransportConfigurator.Configure(
        kestrel,
        socketPathOverride: builder.Configuration["Scout:ControlPlane:SocketPath"],
        pipeName: ControlPlaneEndpoint.DefaultPipeName,
        logger);
});

var app = builder.Build();
app.MapControlPlaneServices();

// Once Kestrel has created the socket, tighten it to 0600. Named pipes are
// ACL'd at creation time, so this only applies to the UDS path.
if (socketPath is not null && !OperatingSystem.IsWindows())
{
    var pathToHarden = socketPath;
    app.Lifetime.ApplicationStarted.Register(() =>
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        try
        {
            ControlPlaneEndpoint.HardenSocket(pathToHarden);
        }
        catch (Exception ex)
        {
            app.Logger.LogWarning(ex, "Failed to harden socket permissions for {SocketPath}", pathToHarden);
        }
    });
}

await app.RunAsync();
