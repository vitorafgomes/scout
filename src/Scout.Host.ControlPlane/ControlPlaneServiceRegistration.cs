using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Scout.ControlPlane;
using Scout.Host.ControlPlane.Security;
using Scout.Host.ControlPlane.Services;

namespace Scout.Host.ControlPlane;

/// <summary>Shared DI wiring for the gRPC control-plane host (used by Program and tests).</summary>
public static class ControlPlaneServiceRegistration
{
    /// <summary>The largest message the host takes or sends, so a full comments view, 1000 comments of up to 2000 characters each, fits.</summary>
    public const int MaxMessageBytes = 16 * 1024 * 1024;

    /// <summary>
    /// Register gRPC, the role-based token interceptor, the caller tokens, the
    /// domain services and the timer that clears old research. Tests pin the desktop <paramref name="token"/> and the
    /// <paramref name="workerToken"/>; with a null <paramref name="token"/> the
    /// desktop token is read from the environment or minted and the worker token
    /// is read from <c>SCOUT_CONTROLPLANE_WORKER_TOKEN</c>.
    /// </summary>
    public static IServiceCollection AddControlPlaneHost(
        this IServiceCollection services,
        IConfiguration config,
        SessionToken? token = null,
        string? workerToken = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(config);

        var desktop = token ?? SessionToken.FromEnvironmentOrMint();
        var worker = token is null
            ? Environment.GetEnvironmentVariable(CallerTokens.WorkerEnvVarName)
            : workerToken;
        services.AddSingleton(sp => CallerTokens.Create(desktop, worker, sp.GetRequiredService<ILogger<CallerTokens>>()));
        services.AddSingleton<TokenAuthInterceptor>();

        services.AddGrpc(options =>
        {
            options.Interceptors.Add<TokenAuthInterceptor>();
            options.MaxReceiveMessageSize = MaxMessageBytes;
            options.MaxSendMessageSize = MaxMessageBytes;
        });

        services.AddControlPlaneDomain(config);
        services.AddHostedService<ResearchCleanupService>();
        return services;
    }

    /// <summary>Map the five gRPC services on the endpoint route builder.</summary>
    public static void MapControlPlaneServices(this Microsoft.AspNetCore.Routing.IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        Microsoft.AspNetCore.Builder.GrpcEndpointRouteBuilderExtensions
            .MapGrpcService<ProvidersGrpcService>(endpoints);
        Microsoft.AspNetCore.Builder.GrpcEndpointRouteBuilderExtensions
            .MapGrpcService<ModelsGrpcService>(endpoints);
        Microsoft.AspNetCore.Builder.GrpcEndpointRouteBuilderExtensions
            .MapGrpcService<FeaturesGrpcService>(endpoints);
        Microsoft.AspNetCore.Builder.GrpcEndpointRouteBuilderExtensions
            .MapGrpcService<WorkerGateGrpcService>(endpoints);
        Microsoft.AspNetCore.Builder.GrpcEndpointRouteBuilderExtensions
            .MapGrpcService<OperatorGrpcService>(endpoints);
    }
}
