namespace Scout.Llm.Abstractions;

/// <summary>
/// A declared write that did not happen: refused by the function's own checks before anything was sent, refused by
/// the system it writes to, or lost to the network. The message is the sentence the model gets; <see cref="Outcome"/>
/// is what the write log keeps after <c>failed:</c>. The write gate filter turns it into the function's answer, so
/// the kernel never sees it.
/// </summary>
public sealed class WriteFailedException(string message, string outcome) : Exception(message)
{
    /// <summary>Why the write did not happen, in a few words for the write log: <c>refused</c>, <c>github 403</c>, <c>HttpRequestException</c>.</summary>
    public string Outcome { get; } = outcome;
}
