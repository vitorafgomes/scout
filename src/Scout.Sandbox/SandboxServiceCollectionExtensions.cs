using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Scout.Sandbox;

/// <summary>
/// DI composition for the test sandbox. Registers <see cref="DockerSandboxRunner"/> with the options under
/// <c>Scout:Sandbox</c>; resolving it touches no file and starts no process.
/// </summary>
public static class SandboxServiceCollectionExtensions
{
    /// <summary>Configuration section read by <see cref="AddSandbox"/>.</summary>
    public const string ConfigSection = "Scout:Sandbox";

    /// <summary>Registers the Docker sandbox runner.</summary>
    public static IServiceCollection AddSandbox(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection(ConfigSection);
        var options = new SandboxOptions();
        if (!string.IsNullOrWhiteSpace(section["WorkDirectory"]))
        {
            options.WorkDirectory = section["WorkDirectory"]!;
        }

        if (!string.IsNullOrWhiteSpace(section["DockerPath"]))
        {
            options.DockerPath = section["DockerPath"]!;
        }

        if (bool.TryParse(section["RestoreNetwork"], out var restoreNetwork))
        {
            options.RestoreNetwork = restoreNetwork;
        }

        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton(sp => new DockerSandboxRunner(options, sp.GetRequiredService<TimeProvider>(), sp.GetService<ILogger<DockerSandboxRunner>>()));
        return services;
    }
}
