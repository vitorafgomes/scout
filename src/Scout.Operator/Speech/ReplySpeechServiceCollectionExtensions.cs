using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.SemanticKernel;
using Scout.Conversation.Abstractions;
using Scout.Llm.Abstractions;

namespace Scout.Operator.Speech;

/// <summary>DI composition for reading a reply aloud.</summary>
public static class ReplySpeechServiceCollectionExtensions
{
    /// <summary>The key naming the folder under Scout's data folder where the local voice renders a sentence before it is read back.</summary>
    public const string DirectoryKey = "Scout:Speech:Directory";

    /// <summary>
    /// Register the <see cref="ISpeechSynthesizer"/>: the connected provider's speech, else the bundled voice named by
    /// <c>Scout:Operator:PiperPath</c> and <c>Scout:Operator:VoicesPath</c>, rendering in the absolute folder at
    /// <see cref="DirectoryKey"/>; without that folder the bundled voice is not used. Needs <c>AddScoutLlm</c>.
    /// </summary>
    public static IServiceCollection AddReplySpeech(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var directory = configuration[DirectoryKey]?.Trim() is { Length: > 0 } configured && Path.IsPathFullyQualified(configured)
            ? configured
            : string.Empty;
        services.AddOptions<OperatorMediaOptions>().Bind(configuration.GetSection(OperatorMediaOptions.Section));
        services.TryAddSingleton<IProcessRunner, ProcessRunner>();
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<PiperSpeech>();
        services.AddSingleton<ISpeechSynthesizer>(sp => new ReplySpeech(
            sp.GetRequiredService<Kernel>(),
            sp.GetRequiredService<MediaCapabilities>(),
            sp.GetRequiredService<PiperSpeech>(),
            directory));
        return services;
    }
}
