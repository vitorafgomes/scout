namespace Scout.Llm.Abstractions;

/// <summary>
/// Signals that a request carries an image and the connected provider's chat service
/// does not accept image parts, so nothing was sent. The message never carries the image.
/// </summary>
public sealed class LlmVisionUnavailableException : Exception
{
    public LlmVisionUnavailableException()
        : base("The connected AI provider cannot read images.")
    {
    }
}
