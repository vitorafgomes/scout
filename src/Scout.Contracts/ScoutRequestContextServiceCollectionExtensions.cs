using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Scout.Contracts;

/// <summary>
/// DI composition for the ambient request context. This is the single, central
/// registration point every consumer calls, so there is exactly ONE shared
/// <see cref="IScoutRequestContext"/> instance across the process. Registering
/// two instances would silently fail closed (the writer and the reader would not
/// share an <see cref="AsyncLocal{T}"/>), so the registration is idempotent via
/// <see cref="ServiceCollectionDescriptorExtensions.TryAddSingleton{TService, TImplementation}(IServiceCollection)"/>.
/// </summary>
public static class ScoutRequestContextServiceCollectionExtensions
{
    /// <summary>
    /// Register the shared ambient request context as a singleton. Idempotent:
    /// repeated calls from independent composition roots keep a single instance.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The same service collection.</returns>
    public static IServiceCollection AddScoutRequestContext(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<IScoutRequestContext, AsyncLocalScoutRequestContext>();
        return services;
    }
}
