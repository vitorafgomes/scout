using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Scout.Conversation.Abstractions;
using Scout.Skills;

namespace Scout.Audit;

/// <summary>DI composition for the whole-repository audit as a job.</summary>
public static class AuditServiceCollectionExtensions
{
    /// <summary>
    /// Register the audit: <see cref="AuditOptions"/> from <c>Scout:Audit</c>, a turn time limit not above zero refused when
    /// the host starts, the database defaulting to <c>Scout/audit.db</c> under the user's local application data; the SQLite store, the batch runner over the shared
    /// kernel and its chat service, the <see cref="IRepositoryAudits"/> that starts, stops and reports a job, with the
    /// origin of the chat turn that starts it, the same instance as the service that starts and reports a pull request
    /// review, and the hosted worker that runs the audits. Needs the kernel of
    /// <c>AddScoutLlm</c>, an <see cref="IOriginNotifier"/>, an <see cref="IAuditSnapshots"/> and an
    /// <see cref="IReviewEnvironment"/>; the GitHub plugin's reads and snapshots are what a job uses, so the host adds this
    /// only when GitHub is configured and adapts its snapshots and the review's environment.
    /// </summary>
    public static IServiceCollection AddAudit(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<AuditOptions>()
            .Bind(configuration.GetSection(AuditOptions.Section))
            .PostConfigure(options =>
            {
                if (string.IsNullOrWhiteSpace(options.DbPath))
                {
                    options.DbPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Scout", "audit.db");
                }
            })
            .Validate(options => options.TurnTimeout > TimeSpan.Zero, "Scout:Audit:TurnTimeout must be above zero; every batch turn would end before the model answers.")
            .ValidateOnStart();

        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<IAuditJobStore>(sp => new SqliteAuditStore(
            sp.GetRequiredService<IOptions<AuditOptions>>().Value.DbPath, sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton<AuditSignal>();
        services.AddSingleton(sp => new AuditBatchRunner(
            sp.GetRequiredService<Kernel>(),
            sp.GetRequiredService<IChatCompletionService>(),
            sp.GetService<ISkillCatalog>(),
            sp.GetRequiredService<IOptions<AuditOptions>>().Value.TurnTimeout,
            sp.GetRequiredService<ILogger<AuditBatchRunner>>()));
        services.AddSingleton(sp => new RepositoryAudits(
            sp.GetRequiredService<IAuditJobStore>(),
            sp.GetRequiredService<AuditSignal>(),
            sp.GetRequiredService<IOptions<AuditOptions>>().Value,
            sp.GetRequiredService<IAuditSnapshots>(),
            sp.GetRequiredService<IReviewEnvironment>(),
            () => sp.GetRequiredService<Kernel>(),
            () => ChatOrigin.Current,
            sp.GetRequiredService<ILogger<RepositoryAudits>>(),
            sp.GetRequiredService<IOriginNotifier>()));
        services.AddSingleton<IRepositoryAudits>(sp => sp.GetRequiredService<RepositoryAudits>());
        services.AddSingleton<IPullRequestReviews>(sp => sp.GetRequiredService<RepositoryAudits>());
        services.AddHostedService<AuditWorker>();
        return services;
    }
}
