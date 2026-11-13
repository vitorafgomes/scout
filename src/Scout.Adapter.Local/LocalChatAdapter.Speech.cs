using Microsoft.Extensions.Logging;
using Scout.Conversation.Abstractions;

namespace Scout.Adapter.Local;

/// <summary>
/// The <c>speak</c> request: one sentence of a reply read aloud in the voice it names, answered with one <c>audio</c>
/// event carrying the content type and the bytes as base64, or a <c>fail</c> saying why; a named voice that is not one the
/// path in use offers is refused before the voice runs. The <c>voices</c> request: one <c>voices</c> event listing them.
/// </summary>
public sealed partial class LocalChatAdapter
{
    internal const int MaxSpeakChars = 2000;
    internal const string NoVoiceReply =
        "No voice: connect an AI Source with speech (OpenAI or Azure OpenAI), or use a Scout Desktop package, which carries Scout's local voice.";
    internal const string SpeakFailedReply = "Scout could not read that aloud; see Scout logs.";
    internal const string VoicesFailedReply = "Scout could not list its voices; see Scout logs.";

    private void Speak(string id, string? text, SpeechChoice choice, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > MaxSpeakChars)
        {
            _logger.LogWarning("Refused local speak {Id}: no text, or more than {Max} characters.", id, MaxSpeakChars);
            Refuse(id, MalformedReply);
            return;
        }

        if (_speech is null)
        {
            _logger.LogInformation("Refused local speak {Id}: no voice is composed.", id);
            Refuse(id, NoVoiceReply);
            return;
        }

        _logger.LogInformation(
            "Local speak {Id} accepted (textLen={Length}, providerVoice={ProviderVoice}, localVoice={LocalVoice}, localOnly={LocalOnly}).",
            id, text.Length, choice.ProviderVoice, choice.LocalVoice, choice.LocalOnly);
        Track(() => SpeakAsync(id, _speech, text, choice, cancellationToken));
    }

    private void ListVoices(string id, CancellationToken cancellationToken)
    {
        if (_speech is null)
        {
            _logger.LogInformation("Refused local voices {Id}: no voice is composed.", id);
            Refuse(id, NoVoiceReply);
            return;
        }

        Track(() => ListVoicesAsync(id, _speech, cancellationToken));
    }

    private async Task ListVoicesAsync(string id, ISpeechSynthesizer speech, CancellationToken cancellationToken)
    {
        try
        {
            var voices = await speech.VoicesAsync(cancellationToken).ConfigureAwait(false);
            _writer.WriteVoices(id, voices);
            _logger.LogInformation(
                "Local voices {Id} answered (provider={Provider}, providerVoices={ProviderVoices}, localVoices={LocalVoices}).",
                id, voices.Provider, voices.ProviderVoices.Count, voices.LocalVoices.Count);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _logger.LogInformation("Local voices {Id} ended with the adapter.", id);
        }
        catch (Exception ex)
        {
            _logger.LogError("Local voices {Id} failed ({Error}).", id, ex.GetType().Name);
            Refuse(id, VoicesFailedReply);
        }
    }

    private static string? UnknownVoice(SpeechVoices voices, SpeechChoice choice)
        => !choice.LocalOnly && voices.ProviderVoices.Count > 0
            ? choice.ProviderVoice is { } provider && !voices.ProviderVoices.Contains(provider, StringComparer.Ordinal) ? provider : null
            : choice.LocalVoice is { } local && voices.LocalVoices.Count > 0 && !voices.LocalVoices.Any(v => v.Id == local) ? local : null;

    private async Task SpeakAsync(string id, ISpeechSynthesizer speech, string text, SpeechChoice choice, CancellationToken cancellationToken)
    {
        try
        {
            if (choice != SpeechChoice.Default && UnknownVoice(await speech.VoicesAsync(cancellationToken).ConfigureAwait(false), choice) is { } unknown)
            {
                _logger.LogInformation("Refused local speak {Id}: the voice {Voice} is not available.", id, unknown);
                Refuse(id, SpeechVoices.Unknown(unknown));
                return;
            }

            if (await speech.SynthesizeAsync(text, choice, cancellationToken).ConfigureAwait(false) is not { } audio)
            {
                _logger.LogInformation("Local speak {Id} refused: this machine has no voice.", id);
                Refuse(id, NoVoiceReply);
                return;
            }

            _writer.WriteAudio(id, audio);
            _logger.LogInformation(
                "Local speak {Id} answered (textLen={Length}, contentType={ContentType}, bytes={Bytes}).",
                id, text.Length, audio.ContentType, audio.Bytes.Length);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _logger.LogInformation("Local speak {Id} ended with the adapter.", id);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogError("Local speak {Id} failed ({Error}, inner {Inner}).", id, ex.Message, ex.InnerException?.GetType().Name);
            Refuse(id, ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError("Local speak {Id} failed ({Error}).", id, ex.GetType().Name);
            Refuse(id, SpeakFailedReply);
        }
    }

    internal ISpeechSynthesizer? Speech => _speech;
}
