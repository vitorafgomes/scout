using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Scout.Conversation.Abstractions;

namespace Scout.Transcription.Whisper;

/// <summary>
/// DI composition for local voice transcription. Registers
/// <see cref="WhisperAudioTranscriber"/> as the <see cref="IAudioTranscriber"/>
/// only when <c>Scout:Transcription:ModelPath</c> names a file that exists;
/// otherwise registers nothing and voice messages get setup guidance.
/// </summary>
public static class WhisperServiceCollectionExtensions
{
    /// <summary>Configuration section read by <see cref="AddWhisperTranscription"/>.</summary>
    public const string ConfigSection = "Scout:Transcription";

    /// <summary>Registers the Whisper transcriber when the configured model file exists.</summary>
    public static IServiceCollection AddWhisperTranscription(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var modelPath = configuration.GetSection(ConfigSection)["ModelPath"];
        if (string.IsNullOrWhiteSpace(modelPath) || !File.Exists(modelPath))
        {
            return services;
        }

        services.AddSingleton<IAudioTranscriber>(_ => new WhisperAudioTranscriber(modelPath));
        return services;
    }
}
