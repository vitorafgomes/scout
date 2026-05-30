using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Scout.Skills;

/// <summary>
/// DI composition for the retrieval-based skills system. Binds
/// <see cref="SkillsOptions"/> and registers an <see cref="ISkillSelector"/>:
/// the embedding-driven <see cref="SkillSelector"/> when enabled, or
/// <see cref="NoOpSkillSelector"/> when disabled. No file I/O, embedding, or
/// network work runs in any factory; the real selector builds its index lazily
/// on first use.
/// </summary>
public static class SkillsServiceCollectionExtensions
{
    /// <summary>
    /// Register the skills system. Code defaults make the feature work without a
    /// <see cref="SkillsOptions.SectionName"/> configuration section present.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">The application configuration.</param>
    /// <returns>The same service collection.</returns>
    public static IServiceCollection AddScoutSkills(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.Configure<SkillsOptions>(configuration.GetSection(SkillsOptions.SectionName));

        // Bind a probe copy to decide which selector to register. Code defaults
        // apply when the section is absent (Enabled defaults to true).
        var options = new SkillsOptions();
        configuration.GetSection(SkillsOptions.SectionName).Bind(options);

        if (options.Enabled)
        {
            services.AddSingleton(sp => new SkillFileLoader(
                sp.GetRequiredService<ILogger<SkillFileLoader>>()));

            services.AddSingleton<ISkillSelector>(sp => new SkillSelector(
                sp.GetRequiredService<SkillFileLoader>(),
                sp.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>(),
                sp.GetRequiredService<IOptions<SkillsOptions>>(),
                sp.GetRequiredService<ILogger<SkillSelector>>()));
        }
        else
        {
            services.AddSingleton<ISkillSelector, NoOpSkillSelector>();
        }

        return services;
    }
}
