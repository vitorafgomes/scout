using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Scout.Conversation.Abstractions;

namespace Scout.Operator.ScreenText;

/// <summary>DI composition for reading the text on a screenshot.</summary>
public static class ScreenTextServiceCollectionExtensions
{
    /// <summary>
    /// Register the bundled <c>tesseract</c> as the <see cref="IScreenTextReader"/>, only when
    /// <see cref="ToolsOptions.TesseractPath"/> names a file and <see cref="ToolsOptions.TessdataPath"/> a folder that
    /// exist; otherwise register nothing, so the local adapter says no text reader is installed.
    /// </summary>
    public static IServiceCollection AddScreenText(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection(ToolsOptions.Section);
        var options = new ToolsOptions
        {
            TesseractPath = section[nameof(ToolsOptions.TesseractPath)]?.Trim() ?? string.Empty,
            TessdataPath = section[nameof(ToolsOptions.TessdataPath)]?.Trim() ?? string.Empty,
            Locale = section[nameof(ToolsOptions.Locale)]?.Trim() is { Length: > 0 } locale ? locale : "en",
        };
        if (!File.Exists(options.TesseractPath) || !Directory.Exists(options.TessdataPath))
        {
            return services;
        }

        services.TryAddSingleton<IProcessRunner, ProcessRunner>();
        services.AddSingleton<IScreenTextReader>(sp => new TesseractScreenReader(sp.GetRequiredService<IProcessRunner>(), options));
        return services;
    }
}
