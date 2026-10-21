using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Scout.Host.ControlPlane.Transport;

/// <summary>Applies a current-user-only ACL to the Windows named pipe.</summary>
[SupportedOSPlatform("windows")]
public static class NamedPipeSecurity
{
    /// <summary>
    /// Configure the named-pipe transport so the pipe grants full control only
    /// to the current user, denying every other principal.
    /// </summary>
    public static void RestrictToCurrentUser(IWebHostBuilder webHost)
    {
        ArgumentNullException.ThrowIfNull(webHost);

        webHost.UseNamedPipes(options =>
        {
            options.CurrentUserOnly = true;
            options.PipeSecurity = BuildCurrentUserAcl();
        });
    }

    private static PipeSecurity BuildCurrentUserAcl()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var owner = identity.User
            ?? throw new InvalidOperationException("Cannot resolve current Windows user SID.");

        var security = new PipeSecurity();
        security.SetOwner(owner);
        security.AddAccessRule(new PipeAccessRule(
            owner,
            PipeAccessRights.FullControl,
            AccessControlType.Allow));
        return security;
    }
}
