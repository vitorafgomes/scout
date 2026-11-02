using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Scout.Conversation.Abstractions;

namespace Scout.Adapter.Local;

/// <summary>
/// DI composition for the local adapter. Registers <see cref="LocalChatAdapter"/>
/// as an <see cref="IChatAdapter"/> only when <c>Scout:Adapters:Local:Enabled</c>
/// is true, which the desktop app sets when it spawns the worker.
/// </summary>
public static class LocalServiceCollectionExtensions
{
    /// <summary>Configuration section read by <see cref="AddLocalAdapter"/>.</summary>
    public const string ConfigSection = "Scout:Adapters:Local";

    /// <summary>Registers the local adapter when it is enabled in configuration.</summary>
    public static IServiceCollection AddLocalAdapter(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        if (!configuration.GetSection(ConfigSection).GetValue<bool>("Enabled"))
        {
            return services;
        }

        services.AddSingleton<LocalChatAdapter>();
        services.AddSingleton<IChatAdapter>(sp => sp.GetRequiredService<LocalChatAdapter>());
        return services;
    }
}
