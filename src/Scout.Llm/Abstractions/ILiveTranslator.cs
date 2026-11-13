namespace Scout.Llm.Abstractions;

/// <summary>
/// Translates one final line of a live transcript through the connected AI Source, as a plain chat completion
/// that offers the model no function. Registered only when a provider is connected.
/// </summary>
public interface ILiveTranslator
{
    /// <summary>
    /// Translate <paramref name="text"/> into <paramref name="targetLanguage"/>, with <paramref name="context"/>, the
    /// lines spoken before it, given only as context; throws when the provider fails or answers nothing.
    /// </summary>
    Task<string> TranslateAsync(string text, IReadOnlyList<string> context, string targetLanguage, CancellationToken ct);
}
