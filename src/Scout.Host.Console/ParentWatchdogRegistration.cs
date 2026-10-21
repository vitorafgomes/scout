using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Scout.Host.Console;

/// <summary>
/// Registration seam for the parent-death watchdog. Kept separate from Program.cs
/// so the env-var gating is unit-testable without bootstrapping the full host.
/// </summary>
internal static class ParentWatchdogRegistration
{
    /// <summary>Env var the Electron supervisor sets to opt the watchdog in.</summary>
    public const string EnableEnvVar = "SCOUT__PARENT_WATCHDOG";

    /// <summary>
    /// Registers the watchdog and its default stdin probe only when
    /// <see cref="EnableEnvVar"/> equals "1". Standalone runs (manual dotnet run,
    /// CLI) leave it unset so a terminal stdin EOF never kills the host.
    /// </summary>
    public static IServiceCollection AddParentWatchdog(
        this IServiceCollection services,
        string? enableFlag)
    {
        if (enableFlag == "1")
        {
            services.AddSingleton<IParentLivenessProbe, StdinEofParentLivenessProbe>();
            services.AddHostedService<ParentWatchdogService>();
        }

        return services;
    }
}
