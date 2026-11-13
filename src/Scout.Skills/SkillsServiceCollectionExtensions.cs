using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;

namespace Scout.Skills;

/// <summary>
/// DI composition for the skills system. Binds <see cref="SkillsOptions"/> and,
/// when enabled, registers the embedding-driven <see cref="SkillSelector"/> for
/// retrieval auto-injection plus the embedding-free <see cref="ISkillCatalog"/>
/// and the tool-based <see cref="SkillsPlugin"/> (a <see cref="KernelPlugin"/>
/// the shared kernel aggregates) for LLM-driven listing and loading. When
/// disabled, only <see cref="NoOpSkillSelector"/> is registered. No file I/O,
/// embedding, or network work runs in any factory; the selector and catalog load
/// lazily on first use.
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

            // Embedding-free catalog backing the tool-based plugin. It depends only
            // on the loader and options, never on the embedder, so the shared kernel
            // can aggregate the Skills plugin without re-entering the embedder.
            services.AddSingleton<ISkillCatalog>(sp => new SkillCatalog(
                sp.GetRequiredService<SkillFileLoader>(),
                sp.GetRequiredService<IOptions<SkillsOptions>>()));

            // Expose skills as callable Semantic Kernel functions. The shared kernel
            // composition aggregates every KernelPlugin singleton.
            services.AddSingleton(sp => KernelPluginFactory.CreateFromObject(
                new SkillsPlugin(sp.GetRequiredService<ISkillCatalog>()),
                "Skills"));
        }
        else
        {
            services.AddSingleton<ISkillSelector, NoOpSkillSelector>();
        }

        return services;
    }
}
