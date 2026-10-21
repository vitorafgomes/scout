namespace Scout.Plugin.CodeReview;

/// <summary>
/// Raised when the language model returns no usable content for a review. The
/// <see cref="Exception.Message"/> is user-safe and suitable for surfacing in
/// chat.
/// </summary>
public sealed class LlmReviewException : Exception
{
    /// <summary>Creates the exception with a user-safe message.</summary>
    public LlmReviewException(string message)
        : base(message)
    {
    }
}
