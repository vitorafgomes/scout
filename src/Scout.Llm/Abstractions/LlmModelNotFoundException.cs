namespace Scout.Llm.Abstractions;

/// <summary>
/// Signals that the AI Source answered HTTP 404 for the model Scout asked for: the model is not installed on it, is
/// still downloading, or does not exist there. <see cref="Model"/> is the model id Scout sent, or "it is set to use"
/// when the chat service names none; neither it nor <see cref="System.Exception.Message"/> is the provider's text.
/// </summary>
public sealed class LlmModelNotFoundException(string? model, Exception? inner = null)
    : Exception($"The AI Source does not have the model {Name(model)} (HTTP 404).", inner)
{
    /// <summary>The model id Scout asked for, or "it is set to use" when it is not known.</summary>
    public string Model { get; } = Name(model);

    private static string Name(string? model) => string.IsNullOrWhiteSpace(model) ? "it is set to use" : model;
}
