using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Scout.ControlPlane;
using Scout.Host.ControlPlane.Security;
using Scout.Host.ControlPlane.Services;

namespace Scout.Host.ControlPlane;

/// <summary>Shared DI wiring for the gRPC control-plane host (used by Program and tests).</summary>
public static class ControlPlaneServiceRegistration
{
    /// <summary>
    /// Register gRPC, the token interceptor, the session token and the domain
    /// services. The caller supplies the <see cref="SessionToken"/> so tests
    /// can pin a known value; pass null to mint/read from the environment.
    /// </summary>
    public static IServiceCollection AddControlPlaneHost(
        this IServiceCollection services,
        IConfiguration config,
        SessionToken? token = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(config);

        services.AddSingleton(token ?? SessionToken.FromEnvironmentOrMint());
        services.AddSingleton<TokenAuthInterceptor>();

        services.AddGrpc(options =>
        {
            options.Interceptors.Add<TokenAuthInterceptor>();
        });

        services.AddControlPlaneDomain(config);
        return services;
    }

    /// <summary>Map the three gRPC services on the endpoint route builder.</summary>
    public static void MapControlPlaneServices(this Microsoft.AspNetCore.Routing.IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        Microsoft.AspNetCore.Builder.GrpcEndpointRouteBuilderExtensions
            .MapGrpcService<ProvidersGrpcService>(endpoints);
        Microsoft.AspNetCore.Builder.GrpcEndpointRouteBuilderExtensions
            .MapGrpcService<ModelsGrpcService>(endpoints);
        Microsoft.AspNetCore.Builder.GrpcEndpointRouteBuilderExtensions
            .MapGrpcService<FeaturesGrpcService>(endpoints);
    }
}
