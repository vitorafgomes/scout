using System.Net;

namespace Scout.Llm.Guard;

/// <summary>
/// Raised when a TypeSafe call fails. The message carries the HTTP status and the
/// provider's validation detail only, never the API key.
/// </summary>
internal sealed class TypeSafeException : Exception
{
    /// <summary>Creates the exception with a key-free message, the status when known, and the cause.</summary>
    public TypeSafeException(string message, HttpStatusCode? statusCode = null, Exception? innerException = null)
        : base(message, innerException)
    {
        StatusCode = statusCode;
    }

    /// <summary>HTTP status that produced the failure, when known.</summary>
    public HttpStatusCode? StatusCode { get; }
}
