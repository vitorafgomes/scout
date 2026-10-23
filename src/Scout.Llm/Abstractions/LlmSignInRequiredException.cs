namespace Scout.Llm.Abstractions;

/// <summary>
/// Signals that the provider needs the user to sign in again: the stored sign-in
/// is missing, unreadable or expired, or the provider rejected it. Neither
/// <see cref="System.Exception.Message"/> nor the inner exception ever carries
/// the credential or the contents of the file holding it.
/// </summary>
public sealed class LlmSignInRequiredException : Exception
{
    public LlmSignInRequiredException(Exception? inner = null)
        : base("ChatGPT sign-in is missing or expired; sign in again on the AI Sources screen.", inner)
    {
    }
}
